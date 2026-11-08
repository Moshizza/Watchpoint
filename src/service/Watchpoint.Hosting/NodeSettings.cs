using System.ComponentModel.DataAnnotations;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Hosting;

/// <summary>
/// Node environment mode.
/// </summary>
public enum NodeEnvironment
{
    /// <summary>Production environment.</summary>
    Production,

    /// <summary>Development environment.</summary>
    Development,
}

/// <summary>
/// Mode of NATS messaging engine.
/// </summary>
public enum NatsMode
{
    /// <summary>Bundled NATS server managed by supervisor.</summary>
    Bundled,

    /// <summary>External NATS server.</summary>
    External,
}

/// <summary>
/// Node settings bound to configuration options.
/// </summary>
public sealed class NodeSettings : IValidatableObject
{
    /// <summary>Node section options.</summary>
    public NodeSection Node { get; set; } = new();

    /// <summary>Path options.</summary>
    public PathsSection Paths { get; set; } = new();

    /// <summary>HTTP endpoint options.</summary>
    public HttpSection Http { get; set; } = new();

    /// <summary>Management endpoint options.</summary>
    public ManagementSection Management { get; set; } = new();

    /// <summary>NATS messaging options.</summary>
    public NatsSection Nats { get; set; } = new();

    /// <summary>Logging options.</summary>
    public LoggingSection Logging { get; set; } = new();

    /// <summary>Telemetry options.</summary>
    public TelemetrySection Telemetry { get; set; } = new();

    /// <summary>Development options.</summary>
    public DevSection Dev { get; set; } = new();

    /// <summary>Shutdown options.</summary>
    public ShutdownSection Shutdown { get; set; } = new();

    /// <summary>Database options.</summary>
    public DatabaseSection Database { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();

        // Node
        if (Node.Id != Guid.Empty && !IsValidV7(Node.Id))
        {
            results.Add(new ValidationResult("Node:Id must be a valid UUIDv7.", [nameof(Node), nameof(Node.Id)]));
        }

        var nameErrors = NameRules.Validate(Node.Name, "/node/name");
        if (nameErrors.Count > 0)
        {
            foreach (var err in nameErrors)
            {
                results.Add(new ValidationResult($"Node:Name is invalid: {err.Code.Value}", [nameof(Node), nameof(Node.Name)]));
            }
        }

        var allowedRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "front", "integration", "pipeline", "history", "media"
        };
        foreach (var role in Node.Roles)
        {
            if (!allowedRoles.Contains(role))
            {
                results.Add(new ValidationResult($"Node:Roles contains unknown role '{role}'.", [nameof(Node), nameof(Node.Roles)]));
            }
        }

        // Http
        if (Http.Urls.Count == 0)
        {
            results.Add(new ValidationResult("Http:Urls must contain at least one URL.", [nameof(Http), nameof(Http.Urls)]));
        }
        else
        {
            foreach (var url in Http.Urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    results.Add(new ValidationResult($"Http:Urls contains invalid URL '{url}'.", [nameof(Http), nameof(Http.Urls)]));
                }
            }
        }

        if (!string.IsNullOrEmpty(Http.Certificate.Path) && !File.Exists(Http.Certificate.Path))
        {
            results.Add(new ValidationResult($"Http:Certificate:Path file does not exist: '{Http.Certificate.Path}'.", [nameof(Http), nameof(Http.Certificate)]));
        }

        // Management
        if (Management.Urls.Count == 0)
        {
            results.Add(new ValidationResult("Management:Urls must contain at least one URL.", [nameof(Management), nameof(Management.Urls)]));
        }
        else
        {
            foreach (var url in Management.Urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                {
                    results.Add(new ValidationResult($"Management:Urls contains invalid URL '{url}'.", [nameof(Management), nameof(Management.Urls)]));
                }
            }
        }

        // Nats
        if (Nats.Port < 1 || Nats.Port > 65535)
        {
            results.Add(new ValidationResult($"Nats:Port must be between 1 and 65535, got {Nats.Port}.", [nameof(Nats), nameof(Nats.Port)]));
        }

        if (!string.IsNullOrEmpty(Nats.Url) && !Uri.TryCreate(Nats.Url, UriKind.Absolute, out _))
        {
            results.Add(new ValidationResult($"Nats:Url contains invalid URL '{Nats.Url}'.", [nameof(Nats), nameof(Nats.Url)]));
        }

        // Logging
        if (Logging.RetainedFileCount < 1 || Logging.RetainedFileCount > 365)
        {
            results.Add(new ValidationResult($"Logging:RetainedFileCount must be between 1 and 365, got {Logging.RetainedFileCount}.", [nameof(Logging), nameof(Logging.RetainedFileCount)]));
        }

        // Telemetry
        if (!string.IsNullOrEmpty(Telemetry.Otlp.Endpoint) && !Uri.TryCreate(Telemetry.Otlp.Endpoint, UriKind.Absolute, out _))
        {
            results.Add(new ValidationResult($"Telemetry:Otlp:Endpoint contains invalid URL '{Telemetry.Otlp.Endpoint}'.", [nameof(Telemetry), nameof(Telemetry.Otlp)]));
        }

        // Shutdown timeout: PT5S to PT5M
        if (Shutdown.TimeoutDuration < TimeSpan.FromSeconds(5) || Shutdown.TimeoutDuration > TimeSpan.FromMinutes(5))
        {
            results.Add(new ValidationResult($"Shutdown:Timeout must be between PT5S and PT5M, got {Shutdown.Timeout}.", [nameof(Shutdown), nameof(Shutdown.Timeout)]));
        }

        return results;
    }

    /// <summary>Checks whether a Guid is a valid UUIDv7.</summary>
    public static bool IsValidV7(Guid value)
    {
        if (value == Guid.Empty)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes, bigEndian: true, out _);
        // version 7: 4 most significant bits of byte 6 are 0111 (0x70)
        var version = bytes[6] >> 4;
        // variant 1 (RFC 4122/9562): 2 most significant bits of byte 8 are 10 (0x80)
        var variant = bytes[8] >> 6;
        return version == 7 && variant == 2;
    }
}

/// <summary>Node section.</summary>
public sealed class NodeSection
{
    /// <summary>Node ID (v7 UUID).</summary>
    public Guid Id { get; set; }

    /// <summary>Node display name.</summary>
    public string Name { get; set; } = System.Environment.MachineName;

    /// <summary>Node roles in cluster.</summary>
    public List<string> Roles { get; set; } = ["front", "integration", "pipeline", "history", "media"];

    /// <summary>Environment mode.</summary>
    public NodeEnvironment Environment { get; set; } = NodeEnvironment.Production;
}

/// <summary>Paths section.</summary>
public sealed class PathsSection
{
    /// <summary>Data directory.</summary>
    public string? Data { get; set; }

    /// <summary>Logs directory.</summary>
    public string? Logs { get; set; }

    /// <summary>Packages directory.</summary>
    public string? Packages { get; set; }

    /// <summary>Binaries directory.</summary>
    public string? Binaries { get; set; }
}

/// <summary>Certificate settings.</summary>
public sealed class CertificateSection
{
    /// <summary>Path to certificate file.</summary>
    public string? Path { get; set; }

    /// <summary>Password or secret reference.</summary>
    public string? Password { get; set; }
}

/// <summary>HTTP settings.</summary>
public sealed class HttpSection
{
    /// <summary>Listening URLs.</summary>
    public List<string> Urls { get; set; } = ["https://0.0.0.0:8443"];

    /// <summary>Certificate options.</summary>
    public CertificateSection Certificate { get; set; } = new();
}

/// <summary>Management endpoints settings.</summary>
public sealed class ManagementSection
{
    /// <summary>Management URLs.</summary>
    public List<string> Urls { get; set; } = ["http://127.0.0.1:9464"];
}

/// <summary>NATS settings.</summary>
public sealed class NatsSection
{
    /// <summary>NATS mode.</summary>
    public NatsMode Mode { get; set; } = NatsMode.Bundled;

    /// <summary>NATS URL.</summary>
    public string Url { get; set; } = "nats://127.0.0.1:4222";

    /// <summary>NATS port.</summary>
    public int Port { get; set; } = 4222;

    /// <summary>NATS JetStream store path.</summary>
    public string? StorePath { get; set; }
}

/// <summary>Logging settings.</summary>
public sealed class LoggingSection
{
    /// <summary>Minimum log level.</summary>
    public string MinimumLevel { get; set; } = "Information";

    /// <summary>Category overrides.</summary>
    public Dictionary<string, string> Overrides { get; set; } = new()
    {
        ["Microsoft.AspNetCore"] = "Warning"
    };

    /// <summary>Number of daily retained log files.</summary>
    public int RetainedFileCount { get; set; } = 31;
}

/// <summary>OTLP telemetry section.</summary>
public sealed class OtlpSection
{
    /// <summary>OTLP endpoint URL.</summary>
    public string? Endpoint { get; set; }
}

/// <summary>Telemetry section.</summary>
public sealed class TelemetrySection
{
    /// <summary>OTLP settings.</summary>
    public OtlpSection Otlp { get; set; } = new();
}

/// <summary>Dev settings.</summary>
public sealed class DevSection
{
    /// <summary>Seed file path.</summary>
    public string? SeedFile { get; set; }
}

/// <summary>Shutdown settings.</summary>
public sealed class ShutdownSection
{
    private TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>Shutdown timeout duration.</summary>
    public string Timeout
    {
        get => System.Xml.XmlConvert.ToString(_timeout);
        set => _timeout = System.Xml.XmlConvert.ToTimeSpan(value);
    }

    /// <summary>Gets the resolved timeout TimeSpan.</summary>
    public TimeSpan TimeoutDuration => _timeout;
}

/// <summary>Database settings.</summary>
public sealed class DatabaseSection
{
    private TimeSpan _startupWait = TimeSpan.FromMinutes(5);

    /// <summary>Duration start-up waits for the database before exit 75.</summary>
    public string StartupWait
    {
        get => System.Xml.XmlConvert.ToString(_startupWait);
        set => _startupWait = System.Xml.XmlConvert.ToTimeSpan(value);
    }

    /// <summary>Gets the resolved startup wait TimeSpan.</summary>
    public TimeSpan StartupWaitDuration => _startupWait;
}
