using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Watchpoint.Secrets.Contracts;

namespace Watchpoint.Secrets.Internal;

/// <summary>Meter <c>Watchpoint.Secrets</c> (spec SEC §11).</summary>
internal sealed class SecretsMetrics
{
    public const string MeterName = "Watchpoint.Secrets";
    private readonly Counter<long> _reads;
    private readonly Counter<long> _denied;
    private readonly Dictionary<string, Func<double?>> _certificates = new(StringComparer.Ordinal);

    public SecretsMetrics(IMeterFactory meters)
    {
        var meter = meters.Create(MeterName);
        _reads = meter.CreateCounter<long>("watchpoint.secrets.reads", "{read}", "Secret reads");
        _denied = meter.CreateCounter<long>("watchpoint.secrets.denied", "{read}", "Denied secret accesses");
        meter.CreateObservableGauge("watchpoint.secrets.cert.days_left", ObserveCertificates, "d", "Days until certificate expiry");
    }

    public void Read(string module) => _reads.Add(1, new KeyValuePair<string, object?>("module", module));

    public void Denied() => _denied.Add(1);

    public void TrackCertificate(string name, Func<double?> daysLeft)
    {
        lock (_certificates)
        {
            _certificates[name] = daysLeft;
        }
    }

    private IEnumerable<Measurement<double>> ObserveCertificates()
    {
        List<KeyValuePair<string, Func<double?>>> certificates;
        lock (_certificates)
        {
            certificates = [.. _certificates];
        }

        foreach (var (name, daysLeft) in certificates)
        {
            if (daysLeft() is { } days)
            {
                yield return new Measurement<double>(days, new KeyValuePair<string, object?>("cert", name));
            }
        }
    }
}

/// <summary>Audit sink used until the audit module exists (T3.06): structured log 2231.</summary>
internal sealed class LoggingSecretsAuditSink(ILogger<LoggingSecretsAuditSink> logger) : ISecretsAuditSink
{
    public void Record(SecretAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        SecretsLog.Audit(logger, record.Action, record.SecretId.Value, record.Module, record.Count);
    }
}

/// <summary>Selects the master key provider (settings or OS default) and loads the key once.</summary>
internal sealed class MasterKeyAccessor(SecretsOptions options, Func<string, string?> environment, ILogger<MasterKeyAccessor> logger)
{
    private readonly Lock _lock = new();
    private MasterKeyRing? _ring;

    public string ProviderName => options.MasterKeyProvider ?? (OperatingSystem.IsWindows() ? DpapiMasterKeyProvider.ProviderName : FileMasterKeyProvider.ProviderName);

    public MasterKeyRing Ring
    {
        get
        {
            lock (_lock)
            {
                return _ring ??= new MasterKeyRing(CreateProvider().LoadOrCreate());
            }
        }
    }

    /// <summary>Replaces the ring (key rotation by <c>wpctl secrets rewrap</c>, tests).</summary>
    public void Use(MasterKeyRing ring)
    {
        lock (_lock)
        {
            _ring = ring;
        }
    }

    private IMasterKeyProvider CreateProvider() => ProviderName switch
    {
        FileMasterKeyProvider.ProviderName => new FileMasterKeyProvider(options, logger),
        DpapiMasterKeyProvider.ProviderName => new DpapiMasterKeyProvider(options, logger),
        EnvironmentMasterKeyProvider.ProviderName => new EnvironmentMasterKeyProvider(environment, logger),
        _ => throw new SecretsException(SecretsErrors.MasterKeyUnavailable(ProviderName, "unknown provider")),
    };
}
