using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Secrets.Contracts;

/// <summary>Reference to a secret in settings and documents: <c>vault:&lt;uuid&gt;</c> or <c>bootstrap:&lt;name&gt;</c>.</summary>
public readonly partial record struct SecretRef
{
    /// <summary>Pattern of a bootstrap secret name.</summary>
    public const string BootstrapNamePattern = "^[a-z][a-z0-9.-]{1,63}$";

    private SecretRef(SecretId? vaultId, string? bootstrapName)
    {
        VaultId = vaultId;
        BootstrapName = bootstrapName;
    }

    /// <summary>Gets whether this is a bootstrap secret.</summary>
    [MemberNotNullWhen(true, nameof(BootstrapName))]
    public bool IsBootstrap => BootstrapName is not null;

    /// <summary>Gets the vault secret id, for <c>vault:</c> references.</summary>
    public SecretId? VaultId { get; }

    /// <summary>Gets the bootstrap name, for <c>bootstrap:</c> references.</summary>
    public string? BootstrapName { get; }

    /// <summary>Parses <c>vault:&lt;uuid&gt;</c> or <c>bootstrap:&lt;name&gt;</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The reference or <c>secrets.ref.invalid</c>.</returns>
    public static Result<SecretRef> Parse(string? text)
    {
        if (text is not null && text.StartsWith("vault:", StringComparison.Ordinal)
            && Guid.TryParseExact(text["vault:".Length..], "D", out var guid) && guid != Guid.Empty)
        {
            return new SecretRef(SecretId.From(guid), null);
        }

        if (text is not null && text.StartsWith("bootstrap:", StringComparison.Ordinal) && IsBootstrapName(text["bootstrap:".Length..]))
        {
            return new SecretRef(null, text["bootstrap:".Length..]);
        }

        return SecretsErrors.RefInvalid();
    }

    /// <summary>A reference to a vault secret.</summary>
    /// <param name="id">The secret.</param>
    /// <returns>The reference.</returns>
    public static SecretRef Vault(SecretId id) => new(id, null);

    /// <summary>A reference to a bootstrap secret.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentException">Invalid name.</exception>
    public static SecretRef Bootstrap(string name) =>
        IsBootstrapName(name) ? new(null, name) : throw new ArgumentException($"Invalid bootstrap secret name '{name}'.", nameof(name));

    /// <summary>Whether <paramref name="name"/> is a valid bootstrap name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>Whether it matches <see cref="BootstrapNamePattern"/>.</returns>
    public static bool IsBootstrapName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Returns the text form.</summary>
    /// <returns><c>vault:&lt;uuid&gt;</c> or <c>bootstrap:&lt;name&gt;</c>.</returns>
    public override string ToString() => IsBootstrap ? "bootstrap:" + BootstrapName : "vault:" + VaultId?.Value.ToString("D");

    [GeneratedRegex(@"^[a-z][a-z0-9.-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
