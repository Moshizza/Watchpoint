using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Publish;
using Watchpoint.Configuration.Internal.Versions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Testing.Logging;

namespace Watchpoint.Configuration.Tests;

/// <summary>
/// The publish path (CFG §7–§8) on fakes: the T3.01 harness (drafts, validation, module object types on a fake live
/// source), one fake applier per module that applies entries to that source when the publish completes, an in-memory
/// version store whose transaction holds a real publish lock, and fakes of the WP_CFG sink, the audit and the scheduler.
/// </summary>
internal sealed class PublishHarness : IDisposable
{
    private readonly ILoggerFactory _loggers;

    public PublishHarness(ConfigurationSettings? settings = null)
    {
        Config = new ConfigHarness(settings);
        _loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        foreach (var module in Config.Types.All.Select(t => t.Module).Distinct())
        {
            Appliers[module] = new FakeApplier(module, Config.Source, Calls);
        }

        Orchestrator = new PublishOrchestrator(Config.Repository, Config.Drafts, Config.Types, Appliers.Values, Store, Time,
            _loggers.CreateLogger<PublishOrchestrator>(), Sink, Audit)
        { Node = Node };
        Publisher = new DraftPublisher(Config.Drafts, Config.Repository, Orchestrator, Config.Types, Config.Authorizer, Config.Approval, Store, Time,
            _loggers.CreateLogger<DraftPublisher>(), Scheduler);
        Versions = new VersionService(Store, Config.Drafts, _loggers.CreateLogger<VersionService>());
    }

    public static Guid Node { get; } = Guid.Parse("0f000000-0000-0000-0000-000000000001");

    public ConfigHarness Config { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 28, 9, 0, 0, TimeSpan.Zero));

    public LogCollector Logs { get; } = new();

    /// <summary>Every applier call, in order: (module, method).</summary>
    public List<(string Module, string Call)> Calls { get; } = [];

    public Dictionary<string, FakeApplier> Appliers { get; } = new(StringComparer.Ordinal);

    public FakePublishStore Store { get; } = new();

    public FakeSink Sink { get; } = new();

    public FakeAudit Audit { get; } = new();

    public FakeScheduler Scheduler { get; } = new();

    public PublishOrchestrator Orchestrator { get; }

    public DraftPublisher Publisher { get; }

    public VersionService Versions { get; }

    public FakeSource Source => Config.Source;

    public static ConfigObjectRef Location(long n) => ConfigHarness.Ref("registry.location", n);

    public static string LocationJson(string name, long? parent = null) =>
        parent is { } p ? $$"""{"name":{"default":"{{name}}"},"kind":"zone","parentId":"{{ConfigHarness.Id(p)}}"}""" : $$"""{"name":{"default":"{{name}}"},"kind":"zone"}""";

    /// <summary>A live location (version <paramref name="version"/>).</summary>
    public void LiveLocation(long n, string name, long version = 1, long? parent = null) =>
        Source.Put("registry.location", ConfigHarness.Id(n), version, LocationJson(name, parent));

    /// <summary>A draft of Alice with the given entries, validated; its current version.</summary>
    public async Task<Draft> DraftAsync(params (string Type, long N, EntryOperation Op, string? Json, long? Base)[] entries)
    {
        var draft = await Config.NewDraftAsync();
        foreach (var (type, n, op, json, @base) in entries)
        {
            draft = await Config.SetAsync(draft, type, n, op, json, @base);
        }

        return draft;
    }

    public async Task<Result<long>> PublishAsync(Draft draft, UserId? actor = null, string? comment = null)
    {
        var changeSet = await Config.Drafts.BuildChangeSetAsync(draft.Id, draft.Owner, CancellationToken.None);
        return changeSet.IsSuccess
            ? await Orchestrator.PublishAsync(changeSet.Value, actor ?? draft.Owner, comment, CancellationToken.None)
            : changeSet.Error;
    }

    public async Task<Draft> ReloadAsync(Draft draft) => (await Config.Repository.GetAsync(draft.Id, CancellationToken.None))!;

    public JsonElement Live(ConfigObjectRef reference) => Source.Documents[reference].Content;

    public void Dispose()
    {
        Orchestrator.Dispose();
        Publisher.Dispose();
        Store.Dispose();
        _loggers.Dispose();
    }
}

/// <summary>
/// A module's applier: prepares the new versions (create 1, update live + 1, delete 0) and applies them to the live
/// source on <c>CompleteApply</c>, as a module swaps its in-memory state after the commit.
/// </summary>
internal sealed class FakeApplier(string module, FakeSource source, List<(string Module, string Call)> calls) : IConfigApplier
{
    private readonly Dictionary<ChangeSetId, IReadOnlyList<AppliedEntry>> _prepared = [];

    public string Module => module;

    /// <summary>Refuse the next prepare with this error code.</summary>
    public string? RefuseWith { get; set; }

    /// <summary>Throw in the next complete.</summary>
    public bool FailComplete { get; set; }

    /// <summary>Awaited inside the prepare (to hold the publish lock).</summary>
    public Func<Task>? DuringPrepare { get; set; }

    public List<IReadOnlyList<DraftEntry>> Prepared { get; } = [];

    public List<DbTransaction> Transactions { get; } = [];

    public List<IReadOnlyList<ConfigObjectRef>> Reloaded { get; } = [];

    public int Aborted { get; private set; }

    public int Completed { get; private set; }

    public async Task<Result<IReadOnlyList<AppliedEntry>>> PrepareApplyAsync(IReadOnlyList<DraftEntry> entries, DbConnection connection, DbTransaction transaction,
        ConfigApplyContext context, CancellationToken ct)
    {
        lock (calls)
        {
            calls.Add((module, "prepare"));
        }

        Prepared.Add(entries);
        Transactions.Add(transaction);
        if (DuringPrepare is { } wait)
        {
            await wait();
        }

        if (RefuseWith is { } code)
        {
            RefuseWith = null;
            return Error.Create(ErrorKind.Unprocessable, code);
        }

        var applied = entries.Select(e =>
        {
            var live = source.Documents.GetValueOrDefault(e.Ref);
            var version = e.Operation switch { EntryOperation.Create => 1, EntryOperation.Update => (live?.Version ?? 0) + 1, _ => 0 };
            return new AppliedEntry(e.Ref, e.Operation, version, live?.Content, e.Operation == EntryOperation.Delete ? null : e.Content);
        }).ToList();
        _prepared[context.ChangeSet] = applied;
        return applied;
    }

    public ValueTask CompleteApplyAsync(IReadOnlyList<AppliedEntry> applied, ConfigApplyContext context, CancellationToken ct)
    {
        lock (calls)
        {
            calls.Add((module, "complete"));
        }

        Completed++;
        if (FailComplete)
        {
            FailComplete = false;
            throw new InvalidOperationException("planned complete failure");
        }

        Apply(applied);
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortApplyAsync(ConfigApplyContext context, CancellationToken ct)
    {
        Aborted++;
        _prepared.Remove(context.ChangeSet);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReloadAsync(IReadOnlyList<ConfigObjectRef> objects, CancellationToken ct)
    {
        Reloaded.Add(objects);

        // Self-healing: the committed state is what was prepared.
        foreach (var applied in _prepared.Values)
        {
            Apply([.. applied.Where(a => objects.Contains(a.Ref))]);
        }

        return ValueTask.CompletedTask;
    }

    private void Apply(IReadOnlyList<AppliedEntry> applied)
    {
        foreach (var entry in applied)
        {
            if (entry.Operation == EntryOperation.Delete)
            {
                source.Documents.Remove(entry.Ref);
            }
            else
            {
                source.Documents[entry.Ref] = new ConfigDocument(entry.Ref, entry.NewVersion, entry.After!.Value);
            }
        }
    }
}

/// <summary>Versions in memory; a transaction holds the publish lock (a semaphore) until committed or disposed.</summary>
internal sealed class FakePublishStore : IPublishStore, IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<VersionRecord> _versions = [];
    private int _inside;

    public int Commits { get; private set; }

    public void Dispose() => _lock.Dispose();

    public int Rollbacks { get; private set; }

    /// <summary>The most publish transactions open at the same time (must stay 1).</summary>
    public int MaxConcurrent { get; private set; }

    public IReadOnlyList<VersionRecord> Versions
    {
        get
        {
            lock (_versions)
            {
                return [.. _versions];
            }
        }
    }

    public async Task<IPublishTransaction> BeginAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        MaxConcurrent = Math.Max(MaxConcurrent, Interlocked.Increment(ref _inside));
        return new FakeTransaction(this);
    }

    public Task<VersionRecord?> GetAsync(long version, CancellationToken ct) => Task.FromResult(Versions.FirstOrDefault(v => v.Version.Version == version));

    public Task<IReadOnlyList<ConfigVersion>> ListAsync(VersionQuery query, int skip, int take, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConfigVersion>>([.. Versions
            .Where(v => (query.Type is null || v.Entries.Any(e => e.Ref.Type == query.Type)) && (query.ObjectId is null || v.Entries.Any(e => e.Ref.Id == query.ObjectId)))
            .OrderByDescending(v => v.Version.Version).Skip(skip).Take(take).Select(v => v.Version)]);

    public Task<IReadOnlyList<VersionRecord>> AfterAsync(long version, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VersionRecord>>([.. Versions.Where(v => v.Version.Version > version).OrderByDescending(v => v.Version.Version)]);

    public Task<AppliedEntry?> ObjectAtAsync(ConfigObjectRef obj, long version, CancellationToken ct) =>
        Task.FromResult(Versions.Where(v => v.Version.Version <= version).OrderByDescending(v => v.Version.Version)
            .SelectMany(v => v.Entries).FirstOrDefault(e => e.Ref == obj));

    public Task<AppliedEntry?> RevisionAsync(ConfigObjectRef obj, long objectVersion, CancellationToken ct) =>
        Task.FromResult(Versions.OrderByDescending(v => v.Version.Version).SelectMany(v => v.Entries)
            .FirstOrDefault(e => e.Ref == obj && e.NewVersion == objectVersion && e.After is not null));

    private sealed class FakeTransaction(FakePublishStore store) : IPublishTransaction
    {
        private VersionRecord? _pending;
        private bool _done;
        private bool _released;

        public DbConnection Connection { get; } = new FakeDbConnection();

        public DbTransaction Transaction { get; } = new FakeDbTransaction();

        public Task<long> NextVersionAsync(CancellationToken ct) => Task.FromResult(store.Versions.Count + 1L);

        public Task WriteAsync(VersionRecord record, CancellationToken ct)
        {
            _pending = record;
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            lock (store._versions)
            {
                store._versions.Add(_pending!);
            }

            store.Commits++;
            _done = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_released)
            {
                return ValueTask.CompletedTask;
            }

            _released = true;
            if (!_done)
            {
                store.Rollbacks++;
            }

            Interlocked.Decrement(ref store._inside);
            store._lock.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeSink : IConfigPublishedSink
{
    public List<(ConfigPublishedBody Body, UserId Actor)> Records { get; } = [];

    public ValueTask AppendAsync(ConfigPublishedBody body, UserId actor, DateTimeOffset at, CancellationToken ct)
    {
        Records.Add((body, actor));
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAudit : IConfigAudit
{
    public List<(ConfigVersion Version, IReadOnlyList<AppliedEntry> Entries)> Records { get; } = [];

    public ValueTask PublishedAsync(ConfigVersion version, IReadOnlyList<AppliedEntry> entries, CancellationToken ct)
    {
        Records.Add((version, entries));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Schedules kept until the test fires them.</summary>
internal sealed class FakeScheduler : IConfigScheduler
{
    public Dictionary<string, (DateTimeOffset At, Func<CancellationToken, ValueTask> Callback)> Pending { get; } = [];

    public IDisposable At(string key, DateTimeOffset when, Func<CancellationToken, ValueTask> callback)
    {
        Pending[key] = (when, callback);
        return new Handle(() => Pending.Remove(key));
    }

    public async Task FireAsync(string key)
    {
        var (_, callback) = Pending[key];
        Pending.Remove(key);
        await callback(CancellationToken.None);
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

[SuppressMessage("Design", "CA1065:Do not raise exceptions in unexpected locations", Justification = "Test double: never opened.")]
internal sealed class FakeDbConnection : DbConnection
{
    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override ConnectionState State => ConnectionState.Open;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close()
    {
    }

    public override void Open()
    {
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeDbTransaction();

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}

internal sealed class FakeDbTransaction : DbTransaction
{
    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => null;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}

/// <summary>JSON helpers of the tests.</summary>
internal static class Json
{
    public static string Text(JsonElement element) => JsonNode.Parse(element.GetRawText())!.ToJsonString();

    public static LocalizedName Name(string text) => LocalizedName.Of(text);
}
