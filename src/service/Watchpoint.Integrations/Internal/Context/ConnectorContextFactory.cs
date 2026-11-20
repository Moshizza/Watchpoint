using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Sdk.Scheduling;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Hosting;
using Watchpoint.Model.Documents;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Registry.Contracts;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Integrations.Internal.Context;

/// <summary>The services of one connector instance; disposed by the runtime when the instance stops.</summary>
internal sealed class InstanceServices
{
    public required ConnectorContext Context { get; init; }

    public required ObservationSink Sink { get; init; }

    public required DeviceMap Map { get; init; }

    public required ConnectorScheduler Scheduler { get; init; }

    public required RequestGate Gate { get; init; }

    public required InboundEndpoints Inbound { get; init; }

    public required ConnectorHttp Http { get; init; }

    /// <summary>Releases everything but the connector: loops, inbound registrations, then the sink (drained within <paramref name="drain"/>).</summary>
    public async Task ReleaseAsync(TimeSpan drain)
    {
        Scheduler.Dispose();
        await Inbound.DisposeAsync().ConfigureAwait(false);
        await Sink.CloseAsync(drain).ConfigureAwait(false);
        Gate.Dispose();
    }
}

/// <summary>Assembles the connector context of an instance (spec IRT §8) from the node's services.</summary>
internal sealed class ConnectorContextFactory(IObservationIngest ingest, IRegistryReader registry, ISecretVault vault, InboundRouter router,
    ConnectorMeter meter, ConnectionsOptions options, TimeProvider time, ILoggerFactory loggers)
{
    public const string AllowInsecureHttpSetting = "allowInsecureHttp";
    public const string InboundTcpSetting = "inboundTcp";
    public const string MaxConcurrencySetting = "maxConcurrency";
    public const int DefaultMaxConcurrency = 4;

    public TimeProvider Time => time;

    /// <summary>The settings document with the schema's defaults applied (spec IRT §8).</summary>
    public static JsonElement EffectiveSettings(ConnectionSettingsSchema schema, JsonElement settings) =>
        new ParameterSchema(schema.Settings).ApplyDefaults(settings);

    public InstanceServices Create(Connection connection, ConnectionSettingsSchema schema, HostedConnector hosted, IConnectionStore store,
        ConnectionCounterSource counters, Action<Integration.Abstractions.Context.ConnectionStatus, ConnectorMessage?> health, Action<InventorySnapshot> inventory,
        CancellationToken stopping, IReadOnlyDictionary<string, string>? plainSecrets = null, bool loadDevices = true)
    {
        var id = connection.Id;
        var package = connection.PackageId.Value;
        var logger = loggers.CreateLogger("Watchpoint.Integrations.Connections");
        var settings = EffectiveSettings(schema, connection.Settings);
        var redaction = new SecretRedaction([.. schema.Secrets.Select(s => s.Id)]);
        var secrets = new SecretAccessor(id, connection.Secrets, vault, redaction, logger, plainSecrets);
        var map = new DeviceMap(logger, id.Value);
        if (loadDevices)
        {
            map.Update(DeviceMapSnapshot.Load(registry, id));
        }

        var sink = new ObservationSink(id, ingest, map, counters, inventory, options.SinkBuffer, time, logger);
        var allowHttp = Flag(settings, AllowInsecureHttpSetting);
        if (allowHttp)
        {
            IntegrationLog.InsecureHttpAllowed(logger, id.Value);
        }

        var http = new ConnectorHttp(allowHttp);
        var maxConcurrency = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(MaxConcurrencySetting, out var max)
            && max.TryGetInt32(out var value) && value > 0 ? value : DefaultMaxConcurrency;
        var gate = new RequestGate(maxConcurrency);
        var scheduler = new ConnectorScheduler(sink, time);
        var inbound = new InboundEndpoints(id, router, secrets, schema.Secrets.Select(s => s.Id).ToHashSet(StringComparer.Ordinal),
            hosted.Permissions.Contains(InboundEndpoints.InboundTcpPermission), Flag(settings, InboundTcpSetting), logger);
        var traceUntil = connection.ProtocolTraceUntil;
        var logging = new ConnectorLoggerFactory(loggers, id.Value, package, redaction, () => traceUntil is { } until && until > time.GetUtcNow());
        var context = new ConnectorContext(
            new ConnectionInfo(id.Value, connection.Name.Default, package, connection.PackageVersion.ToString(), connection.ServerType, options.Culture),
            settings, secrets, sink, map, store, scheduler, gate, new ConnectionHealthReporter(health), http, inbound,
            new ConnectorMetrics(meter, id.Value.ToString("D"), package), logging, time, stopping);
        return new InstanceServices { Context = context, Sink = sink, Map = map, Scheduler = scheduler, Gate = gate, Inbound = inbound, Http = http };
    }

    private static bool Flag(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
}
