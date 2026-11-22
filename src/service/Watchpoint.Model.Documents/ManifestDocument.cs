using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model.Documents;

public enum PackageIsolation { InProcessAllowed, IsolatedRecommended, IsolatedRequired }

public enum ManifestLicenceClass { Included, Standard, Advanced }

public sealed record ManifestLicenceDoc(ManifestLicenceClass Class, string? FeatureKey = null);

public sealed record SecretFieldDoc(string Id, string Kind, bool Required);

public sealed record SecretsSchemaDoc(IReadOnlyList<SecretFieldDoc> Fields);

public sealed record ConnectorLimitsDoc(
    int? DevicesPerConnection = null,
    int? CommandsPerSecond = null,
    int? MaxConcurrency = null,
    TimeSpan? SilenceTimeout = null);

public sealed record ConnectorDefinitionDoc(
    string Class,
    string ServerType,
    string SupportedVendorVersions,
    IReadOnlyList<string> Features,
    string? SettingsSchemaPath = null,
    ParameterSchema? InlineSettingsSchema = null,
    SecretsSchemaDoc? SecretsSchema = null,
    ConnectorLimitsDoc? Limits = null);

public sealed record PackagePrivacyDoc(IReadOnlyList<string>? PersonalData = null);

public sealed record PackageFileDoc(string Path, string Sha256);

public sealed record ManifestDocument(
    PackageId Id,
    SemVersion Version,
    string Vendor,
    Category Category,
    SemVersionRange Contract,
    SemVersion MinWatchpointVersion,
    IReadOnlyList<string> Platforms,
    bool NativeCode,
    PackageIsolation Isolation,
    ManifestLicenceDoc Licence,
    IReadOnlyList<ConnectorDefinitionDoc> Connectors,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<string> RuleTemplates,
    IReadOnlyList<PackageFileDoc> Files,
    string? Symbols = null,
    string? I18n = null,
    string? Docs = null,
    PackagePrivacyDoc? Privacy = null,
    IReadOnlyList<string>? Permissions = null);
