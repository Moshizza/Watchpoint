using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// Reads a child's stdout and stderr line by line and logs them (spec HST §7.3). Memory is bounded: a line is cut
/// at <see cref="MaxLineLength"/> characters and at most <c>capacity</c> lines wait for the logger; when the logger
/// is slower than the child, new lines are dropped and counted (event 1155). The pipes are always drained, so a
/// chatty child never blocks on a full pipe.
/// </summary>
internal sealed class ChildOutputPump : IDisposable
{
    /// <summary>Lines longer than 8 KB (8,192 characters) are truncated.</summary>
    public const int MaxLineLength = 8192;

    /// <summary>Appended to a truncated line.</summary>
    public const string TruncationMarker = " …[truncated]";

    private readonly string _child;
    private readonly ILogger _childLogger;
    private readonly ILogger _hostLogger;
    private readonly LogLevel _stderrLevel;
    private readonly Channel<(bool IsError, string Text)> _lines;
    private readonly CancellationTokenSource _abort = new();
    private readonly Lock _gate = new();
    private readonly List<(string Contains, TaskCompletionSource<bool> Seen)> _waiters = [];
    private long _dropped;
    private Task _readers = Task.CompletedTask;
    private Task _writer = Task.CompletedTask;

    public ChildOutputPump(string child, ILogger childLogger, ILogger hostLogger, LogLevel stderrLevel, int capacity)
    {
        _child = child;
        _childLogger = childLogger;
        _hostLogger = hostLogger;
        _stderrLevel = stderrLevel;
        _lines = Channel.CreateBounded<(bool, string)>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
    }

    public void Start(Stream? stdout, Stream? stderr)
    {
        var readers = new List<Task>(2);
        if (stdout is not null)
        {
            readers.Add(Task.Run(() => ReadLinesAsync(stdout, isError: false)));
        }

        if (stderr is not null)
        {
            readers.Add(Task.Run(() => ReadLinesAsync(stderr, isError: true)));
        }

        _readers = Task.WhenAll(readers).ContinueWith(
            _ => _lines.Writer.TryComplete(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _writer = Task.Run(WriteAsync);
    }

    /// <summary>Completes with <see langword="true"/> when a stdout line containing <paramref name="contains"/> is read.</summary>
    public Task<bool> WaitForLineAsync(string contains, CancellationToken ct)
    {
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _waiters.Add((contains, seen));
        }

        ct.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(), seen);
        return seen.Task;
    }

    /// <summary>
    /// Waits until both streams reached their end and every queued line is logged, at most <paramref name="timeout"/>
    /// (a descendant that inherited the pipe may keep it open); then abandons the streams.
    /// </summary>
    public async Task CompleteAsync(TimeSpan timeout, TimeProvider timeProvider)
    {
        var done = Task.WhenAll(_readers, _writer);
        try
        {
            await done.WaitAsync(timeout, timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _abort.CancelAsync().ConfigureAwait(false);
            _lines.Writer.TryComplete();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reader and writer loops do not throw; nothing to report.
        }
    }

    public void Dispose() => _abort.Dispose();

    private async Task ReadLinesAsync(Stream stream, bool isError)
    {
        var bytes = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var line = new char[MaxLineLength];
        var length = 0;
        var truncated = false;
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(bytes, _abort.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                for (var i = 0; i < count; i++)
                {
                    var c = chars[i];
                    if (c == '\n')
                    {
                        Emit(isError, line, length, truncated);
                        length = 0;
                        truncated = false;
                    }
                    else if (length < MaxLineLength)
                    {
                        line[length++] = c;
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The pipe broke or the pump was abandoned: keep what was read.
        }

        Emit(isError, line, length, truncated);
    }

    private void Emit(bool isError, char[] buffer, int length, bool truncated)
    {
        if (length > 0 && buffer[length - 1] == '\r')
        {
            length--;
        }

        if (length == 0 && !truncated)
        {
            return;
        }

        var text = truncated ? string.Concat(buffer.AsSpan(0, length), TruncationMarker) : new string(buffer, 0, length);
        if (!isError)
        {
            NotifyWaiters(text);
        }

        _lines.Writer.TryWrite((isError, text));
    }

    private void NotifyWaiters(string text)
    {
        lock (_gate)
        {
            for (var i = _waiters.Count - 1; i >= 0; i--)
            {
                if (text.Contains(_waiters[i].Contains, StringComparison.Ordinal))
                {
                    _waiters[i].Seen.TrySetResult(true);
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    private async Task WriteAsync()
    {
        await foreach (var (isError, text) in _lines.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                HostingLog.LogChildOutput(_childLogger, isError ? _stderrLevel : LogLevel.Information, _child, text);
                ReportDropped();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failing log sink must not stop the pump: the pipes still have to be drained.
            }
        }

        ReportDropped();
    }

    private void ReportDropped()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            HostingLog.LogChildOutputDropped(_hostLogger, _child, dropped);
        }
    }
}
