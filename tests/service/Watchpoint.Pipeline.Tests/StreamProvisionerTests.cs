using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NATS.Client.JetStream.Models;
using Shouldly;
using Watchpoint.Hosting;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal;
using Watchpoint.Pipeline.Internal.Nats;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class StreamProvisionerTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;

    private readonly PipelineHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task PIP_U_016_Stream_provisioner_creates_three_streams_with_settings()
    {
        var provisioner = Provisioner(_harness.Options);
        provisioner.Ready.ShouldBeFalse();

        await provisioner.ExecuteAsync(Ct);

        provisioner.Phase.ShouldBe(StartupPhase.Infrastructure);
        provisioner.Order.ShouldBe(20);
        provisioner.Ready.ShouldBeTrue();
        _harness.Broker.Streams.Keys.Order().ShouldBe(["T1_WP_CFG", "T1_WP_LOG", "T1_WP_SYS"]);
        var log = _harness.Broker.Streams["T1_WP_LOG"];
        log.Subjects.ShouldBe(["t1.wp.log.>"]);
        log.MaxAge.ShouldBe(TimeSpan.FromDays(7));
        log.MaxBytes.ShouldBe(20 * Gb);
        foreach (var (name, subject) in new[] { ("T1_WP_CFG", "t1.wp.cfg.>"), ("T1_WP_SYS", "t1.wp.sys.>") })
        {
            var global = _harness.Broker.Streams[name];
            global.Subjects.ShouldBe([subject]);
            global.MaxAge.ShouldBe(TimeSpan.FromDays(30));
            global.MaxBytes.ShouldBe(2 * Gb);
        }

        foreach (var stream in _harness.Broker.Streams.Values)
        {
            stream.DuplicateWindow.ShouldBe(TimeSpan.FromMinutes(2));
            stream.MaxMessageSize.ShouldBe(1024 * 1024);
            stream.Replicas.ShouldBe(1);
            (stream.FileStorage, stream.LimitsRetention, stream.DiscardOld, stream.AllowDirect, stream.DenyDelete, stream.DenyPurge)
                .ShouldBe((true, true, true, true, true, true));
        }

        // The JetStream form of these settings.
        var config = NatsLogBroker.ToConfig(log);
        config.Name.ShouldBe("T1_WP_LOG");
        config.Subjects.ShouldBe(["t1.wp.log.>"]);
        config.Storage.ShouldBe(StreamConfigStorage.File);
        config.Retention.ShouldBe(StreamConfigRetention.Limits);
        config.Discard.ShouldBe(StreamConfigDiscard.Old);
        config.DuplicateWindow.ShouldBe(TimeSpan.FromMinutes(2));
        config.MaxMsgSize.ShouldBe(1024 * 1024);
        config.MaxAge.ShouldBe(TimeSpan.FromDays(7));
        config.MaxBytes.ShouldBe(20 * Gb);
        config.NumReplicas.ShouldBe(1);
        (config.AllowDirect, config.DenyDelete, config.DenyPurge).ShouldBe((true, true, true));

        // Idempotent: a second start changes nothing.
        _harness.Broker.ManagementCalls.Clear();
        await Provisioner(_harness.Options).ExecuteAsync(Ct);
        _harness.Broker.ManagementCalls.ShouldBe(["get T1_WP_CFG", "get T1_WP_SYS", "get T1_WP_LOG"]);
        _harness.Logs.WithEventId(1601).Count().ShouldBe(2);
    }

    [Fact]
    public async Task PIP_U_017_Incompatible_stream_detected()
    {
        var desired = Provisioner(_harness.Options).DesiredStreams().Single(s => s.Name == "T1_WP_LOG");
        _harness.Broker.AddStream(desired with { Subjects = ["other.log.>"] });

        var ex = await Should.ThrowAsync<PipelineException>(() => Provisioner(_harness.Options).ExecuteAsync(Ct));

        ex.Error.Code.Value.ShouldBe("pipeline.stream.incompatible");
        ex.Error.Kind.ShouldBe(Kernel.Results.ErrorKind.Internal);
        ex.Error.Args["stream"].ShouldBe("T1_WP_LOG");
        ex.Message.ShouldContain("other.log.>");
        _harness.Broker.ManagementCalls.ShouldNotContain("update T1_WP_LOG"); // the stream is left as it is, start-up stops
        _harness.Broker.ManagementCalls.ShouldNotContain("create T1_WP_LOG");
        _harness.Broker.Streams["T1_WP_LOG"].Subjects.ShouldBe(["other.log.>"]);

        // Settings JetStream cannot change are incompatible too.
        foreach (var immutable in new[] { desired with { FileStorage = false }, desired with { DenyDelete = false }, desired with { DenyPurge = false }, desired with { LimitsRetention = false } })
        {
            _harness.Broker.AddStream(immutable);
            var provisioner = Provisioner(_harness.Options);
            (await Should.ThrowAsync<PipelineException>(() => provisioner.ExecuteAsync(Ct))).Error.Code.Value.ShouldBe("pipeline.stream.incompatible");
            provisioner.Ready.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task PIP_U_018_Retention_and_size_settings_applied()
    {
        var options = PipelineOptions.FromConfiguration(Configuration(
            ("Log:Retention", "P3D"), ("Log:MaxBytes", "5GB"), ("Log:SubjectPrefix", "t1.wp"), ("Log:StreamPrefix", "T1_")));
        options.Retention.ShouldBe(TimeSpan.FromDays(3));
        options.MaxBytes.ShouldBe(5 * Gb);

        // Created with the configured values.
        await Provisioner(options).ExecuteAsync(Ct);
        _harness.Broker.Streams["T1_WP_LOG"].MaxAge.ShouldBe(TimeSpan.FromDays(3));
        _harness.Broker.Streams["T1_WP_LOG"].MaxBytes.ShouldBe(5 * Gb);

        // Changed settings are applied to the existing stream (drift), the other streams are left alone.
        _harness.Broker.ManagementCalls.Clear();
        var changed = PipelineOptions.FromConfiguration(Configuration(
            ("Log:Retention", "P10D"), ("Log:MaxBytes", "8589934592"), ("Log:SubjectPrefix", "t1.wp"), ("Log:StreamPrefix", "T1_")));
        await Provisioner(changed).ExecuteAsync(Ct);
        _harness.Broker.Streams["T1_WP_LOG"].MaxAge.ShouldBe(TimeSpan.FromDays(10));
        _harness.Broker.Streams["T1_WP_LOG"].MaxBytes.ShouldBe(8 * Gb);
        _harness.Broker.ManagementCalls.ShouldBe(["get T1_WP_CFG", "get T1_WP_SYS", "get T1_WP_LOG", "update T1_WP_LOG"]);
        _harness.Logs.WithEventId(1603).ShouldHaveSingleItem().Properties["Stream"].ShouldBe("T1_WP_LOG");

        // JetStream reserves the size of a stream: with less storage than configured the size is halved until it fits.
        using var small = new PipelineHarness();
        small.Broker.StorageLimit = 10 * Gb;
        await new StreamProvisioner(small.Broker, small.Options, small.Time, small.Logger("test").As<StreamProvisioner>()).ExecuteAsync(Ct);
        small.Broker.Streams["T1_WP_LOG"].MaxBytes.ShouldBe(5 * Gb);
        small.Broker.Streams["T1_WP_CFG"].MaxBytes.ShouldBe(2 * Gb);
        var reduced = small.Logs.WithEventId(1604).ShouldHaveSingleItem();
        reduced.Level.ShouldBe(LogLevel.Warning);
        reduced.Properties["Bytes"].ShouldBe(5 * Gb);

        // Out-of-range settings are refused when the configuration is read.
        foreach (var (key, value) in new[]
        {
            ("Log:Retention", "PT1H"), ("Log:Retention", "P91D"), ("Log:Retention", "three days"), ("Log:MaxBytes", "512MB"),
            ("Log:Replicas", "2"), ("Log:SubjectPrefix", "Bad Prefix"), ("Log:StreamPrefix", "lower"), ("Log:AppendTimeout", "PT5M"),
        })
        {
            Should.Throw<InvalidOperationException>(() => PipelineOptions.FromConfiguration(Configuration((key, value)))).Message.ShouldContain(key);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private StreamProvisioner Provisioner(PipelineOptions options) =>
        new(_harness.Broker, options, _harness.Time, _harness.Logger("test").As<StreamProvisioner>());
}

internal static class LoggerExtensions
{
    /// <summary>Adapts a logger to the typed logger a class asks for.</summary>
    public static ILogger<T> As<T>(this ILogger logger) => new TypedLogger<T>(logger);

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
