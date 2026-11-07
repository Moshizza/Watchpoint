namespace Watchpoint.Secrets.Contracts;

/// <summary>What a secret is (informative; all kinds are encrypted the same way).</summary>
public enum SecretKind
{
    /// <summary>A password.</summary>
    Password,

    /// <summary>An API key.</summary>
    ApiKey,

    /// <summary>A token.</summary>
    Token,

    /// <summary>A certificate (with its private key).</summary>
    Certificate,

    /// <summary>A private key.</summary>
    PrivateKey,

    /// <summary>Anything else.</summary>
    Generic,
}

/// <summary>The component allowed to use a secret: a module and, optionally, one of its entities.</summary>
/// <param name="Module">Module name, compared ordinally, e.g. <c>Integrations</c>.</param>
/// <param name="EntityId">The entity (e.g. a connection id); <see langword="null"/> = any entity of the module.</param>
public sealed record SecretOwner(string Module, Guid? EntityId);
