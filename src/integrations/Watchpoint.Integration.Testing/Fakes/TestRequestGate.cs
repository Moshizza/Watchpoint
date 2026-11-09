using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Fake implementation of <see cref="IRequestGate"/> supporting priority order inspection.
/// </summary>
public sealed class TestRequestGate : IRequestGate, IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>Order of command priorities as they entered execution.</summary>
    public List<CommandPriority> ExecutedPriorities { get; } = [];

    /// <inheritdoc />
    public int MaxConcurrency { get; }

    /// <summary>Initializes a new instance of <see cref="TestRequestGate"/>.</summary>
    public TestRequestGate(int maxConcurrency = 4)
    {
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <inheritdoc />
    public async ValueTask<T> RunAsync<T>(CommandPriority priority, Func<CancellationToken, ValueTask<T>> request, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            lock (ExecutedPriorities)
            {
                ExecutedPriorities.Add(priority);
            }
            return await request(ct);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
