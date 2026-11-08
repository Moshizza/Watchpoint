using Microsoft.Extensions.Logging;

namespace Watchpoint.Secrets;

/// <summary>Log events of the secrets module (spec SEC §11, event ids 2200–2299). Values are never logged.</summary>
internal static partial class SecretsLog
{
    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Master key {KeyId} loaded from provider {Provider}")]
    public static partial void MasterKeyLoaded(ILogger logger, string keyId, string provider);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Information, Message = "Master key {KeyId} created with provider {Provider}")]
    public static partial void MasterKeyCreated(ILogger logger, string keyId, string provider);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Warning, Message = "Master key file permissions too wide: {Path}")]
    public static partial void MasterKeyPermissionsTooWide(ILogger logger, string path);

    [LoggerMessage(EventId = 2210, Level = LogLevel.Debug, Message = "Secret {SecretId} read by {Module} for {Purpose}")]
    public static partial void SecretRead(ILogger logger, Guid secretId, string module, string purpose);

    [LoggerMessage(EventId = 2211, Level = LogLevel.Error, Message = "Secret {SecretId} integrity check failed")]
    public static partial void IntegrityFailed(ILogger logger, string secretId);

    [LoggerMessage(EventId = 2212, Level = LogLevel.Warning, Message = "Secret {SecretId} access denied for {Module}")]
    public static partial void AccessDenied(ILogger logger, Guid secretId, string module);

    [LoggerMessage(EventId = 2220, Level = LogLevel.Information, Message = "Rewrapped {Count} data keys to master key {KeyId}")]
    public static partial void Rewrapped(ILogger logger, int count, string keyId);

    [LoggerMessage(EventId = 2230, Level = LogLevel.Information, Message = "Legacy NATS token imported as bootstrap secret nats.token")]
    public static partial void NatsTokenImported(ILogger logger);

    [LoggerMessage(EventId = 2231, Level = LogLevel.Information, Message = "Secret audit: {Action} {SecretId} by {Module} ({Count})")]
    public static partial void Audit(ILogger logger, string action, Guid secretId, string module, int count);

    [LoggerMessage(EventId = 2240, Level = LogLevel.Information, Message = "Node certificate renewed, expires {NotAfter}")]
    public static partial void NodeCertificateRenewed(ILogger logger, DateTimeOffset notAfter);

    [LoggerMessage(EventId = 2241, Level = LogLevel.Warning, Message = "Site CA expires on {NotAfter}")]
    public static partial void CaExpiresSoon(ILogger logger, DateTimeOffset notAfter);

    [LoggerMessage(EventId = 2242, Level = LogLevel.Information, Message = "Site CA {Subject} created, fingerprint {Fingerprint}")]
    public static partial void CaCreated(ILogger logger, string subject, string fingerprint);
}
