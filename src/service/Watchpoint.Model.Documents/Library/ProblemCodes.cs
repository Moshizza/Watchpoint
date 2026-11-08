namespace Watchpoint.Model.Documents.Library;

/// <summary>Codes of the cross-document checks (spec SCM §7).</summary>
public static class ProblemCodes
{
#pragma warning disable CS1591 // names are the codes
    public const string CapabilityMissing = "scm.capability.missing";
    public const string CapabilityVersionUnsatisfied = "scm.capability.version_unsatisfied";
    public const string CapabilityDuplicate = "scm.capability.duplicate";
    public const string TypeMissing = "scm.type.missing";
    public const string TypeDuplicate = "scm.type.duplicate";
    public const string TypeExtendsCycle = "scm.type.extends_cycle";
    public const string TypeExtendsTooDeep = "scm.type.extends_too_deep";
    public const string TypeHealthMissing = "scm.type.health_missing";
    public const string TypePrimaryPropertyUnknown = "scm.type.primary_property_unknown";
    public const string TypeOverridePropertyUnknown = "scm.type.override_property_unknown";
    public const string TypeOverrideValueUnknown = "scm.type.override_value_unknown";
    public const string TypeExcludeUnknown = "scm.type.exclude_unknown";
    public const string CommandEffectPropertyUnknown = "scm.command.effect_property_unknown";
    public const string PropertyWritableWithoutSetter = "scm.property.writable_without_setter";
    public const string TranslationMissing = "scm.i18n.key_missing";
    public const string SymbolMissing = "scm.symbol.missing";
    public const string SymbolUnsafe = "scm.symbol.unsafe";
    public const string ManifestFileHashMismatch = "scm.manifest.file_hash_mismatch";
    public const string ManifestFileUnlisted = "scm.manifest.file_unlisted";
    public const string ManifestFileMissing = "scm.manifest.file_missing";
    public const string ManifestNativeRequiresIsolation = "scm.manifest.native_requires_isolation";
    public const string ManifestConnectorAssemblyUnlisted = "scm.manifest.connector_assembly_unlisted";
    public const string RuleTemplateUnresolved = "scm.rule.template_unresolved";
    public const string DocumentInvalid = "scm.document.invalid";
#pragma warning restore CS1591
}
