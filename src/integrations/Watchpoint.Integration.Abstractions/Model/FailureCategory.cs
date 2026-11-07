namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>Why an operation of a connector failed (INT §4); mapped by the runtime to errors and health.</summary>
public enum FailureCategory
{
    /// <summary>The system cannot be reached (network, DNS, refused connection).</summary>
    Unreachable,

    /// <summary>Credentials were refused.</summary>
    AuthenticationFailed,

    /// <summary>The system's version is not supported by the connector.</summary>
    IncompatibleVersion,

    /// <summary>The vendor system lacks the licence needed for the integration.</summary>
    VendorLicenceMissing,

    /// <summary>TLS negotiation or certificate validation failed.</summary>
    TlsError,

    /// <summary>The settings are invalid for this system.</summary>
    InvalidSettings,

    /// <summary>The target device is offline.</summary>
    DeviceOffline,

    /// <summary>The connection to the system is down.</summary>
    ConnectionDown,

    /// <summary>The system refused the operation (business rule, permission on the vendor side).</summary>
    RefusedBySystem,

    /// <summary>The device or system is in a state where the operation is not possible.</summary>
    InvalidState,

    /// <summary>The operation is not supported.</summary>
    NotSupported,

    /// <summary>The operation did not complete in time.</summary>
    Timeout,

    /// <summary>The operation conflicts with another operation or a concurrent change.</summary>
    Conflict,

    /// <summary>An unexpected failure inside the connector.</summary>
    InternalError,
}
