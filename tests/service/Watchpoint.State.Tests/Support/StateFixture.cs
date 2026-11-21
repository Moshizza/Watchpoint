using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Watchpoint.Kernel.Testing;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Internal;
using Watchpoint.State.Tests.Fakes;

namespace Watchpoint.State.Tests.Support;

/// <summary>Runs posted partition work synchronously, in place (spec STA §13's unit layer has no real pipeline).</summary>
internal sealed class SynchronousPartitionWork : IPartitionWork
{
    public Task RunAsync(int partition, Action work, CancellationToken ct)
    {
        work();
        return Task.CompletedTask;
    }
}

/// <summary>Wires the state engine with fakes, for unit tests (spec STA §13).</summary>
internal sealed class StateFixture
{
    public StateFixture(int ringCapacity = 10_000)
    {
        Time = TestTime.CreateProvider();
        Registry = new FakeRegistryReader();
        Types = new FakeTypeCatalog();
        Evaluator = new FakeStateSummaryEvaluator();
        RecordTypes = new TestRecordTypeRegistry();
        RecordTypes.Register<PropertyRecordBody>(RecordKinds.Property, 1, null!);
        RecordTypes.Register<EventRecordBody>(RecordKinds.Event, 1, null!);
        RecordTypes.Register<HealthRecordBody>(RecordKinds.Health, 1, null!);
        Records = new RecordBuilders(RecordTypes, Time);
        Aggregates = new AggregateCounters();
        Ring = new RecentChangesRing(ringCapacity);
        Fanout = new ChangeFanout(Ring, NullLogger<ChangeFanout>.Instance);
        Store = new StateStore(Types, Evaluator, Registry, RecordTypes, Aggregates, Ring, Fanout);
        Work = new SynchronousPartitionWork();
        Cascade = new ServerAvailabilityCascade(Registry, Work, Store, Time);
        Subscriber = new StateSubscriber(Store, Cascade);
        Tracker = new MembershipTracker(Registry, Store, Work, Time);
        Tracker.Subscribe();
    }

    public FakeTimeProvider Time { get; }

    public FakeRegistryReader Registry { get; }

    public FakeTypeCatalog Types { get; }

    public FakeStateSummaryEvaluator Evaluator { get; }

    public TestRecordTypeRegistry RecordTypes { get; }

    public RecordBuilders Records { get; }

    public AggregateCounters Aggregates { get; }

    public RecentChangesRing Ring { get; }

    public ChangeFanout Fanout { get; }

    public StateStore Store { get; }

    public IPartitionWork Work { get; }

    public ServerAvailabilityCascade Cascade { get; }

    public StateSubscriber Subscriber { get; }

    public MembershipTracker Tracker { get; }

    /// <summary>
    /// Applies a single record directly on <see cref="Store"/> (spec STA §6.1), and — exactly as
    /// <see cref="StateSubscriber.OnRecords"/> does — runs any resulting server-availability transition through
    /// <see cref="Cascade"/>. <see cref="PartitionContext"/> is internal to <c>Watchpoint.Pipeline</c>, so unit tests
    /// drive the engine directly rather than through <see cref="IPartitionSubscriber"/>.
    /// </summary>
    public void Apply(LogRecord record, bool isRecovering = false) => Apply([record], isRecovering);

    /// <summary>Applies several records of the same partition in one batch.</summary>
    public void Apply(IReadOnlyList<LogRecord> records, bool isRecovering = false)
    {
        var transitions = Store.ApplyRecords(records[0].Envelope.P, Time.GetUtcNow(), isRecovering, records);
        foreach (var (server, unavailable) in transitions)
        {
            Cascade.OnServerHealthChanged(server, unavailable);
        }
    }

    public void Tick(int partition) => Store.Tick(partition, Time.GetUtcNow());

    public void Touch(TouchInfo touch, int partition) => Store.ApplyTouch(partition, Time.GetUtcNow(), touch);
}
