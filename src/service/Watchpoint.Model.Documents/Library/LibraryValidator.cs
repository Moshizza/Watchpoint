using System.Security.Cryptography;
using System.Text.Json;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Model.Documents.Library;

/// <summary>Cross-document checks (spec SCM §7): references, extends chains, effects, translations, symbols, package files.</summary>
public sealed class LibraryValidator : ILibraryValidator
{
    /// <summary>Maximum <c>extends</c> hops.</summary>
    public const int MaxExtendsDepth = 3;

    /// <summary>Overall states that need a symbol (spec SCM §3.1), plus <c>default</c>.</summary>
    public static readonly IReadOnlyList<string> SymbolStates =
        ["alarm", "fault", "warning", "normalActive", "normal", "maintenance", "disabled", "offline", "unknown", "default"];

    /// <inheritdoc/>
    public LibraryValidationReport Validate(LibrarySet library, LibraryValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(options);
        var run = new Run(library, options);
        run.Execute();
        return new LibraryValidationReport([.. run.Problems
            .OrderBy(p => p.SourcePath, StringComparer.Ordinal)
            .ThenBy(p => p.JsonPath, StringComparer.Ordinal)
            .ThenBy(p => p.Code.Value, StringComparer.Ordinal)]);
    }

    /// <summary>Resolves a type of the set with its <c>extends</c> chain.</summary>
    /// <param name="set">The folders.</param>
    /// <param name="typeId">The type.</param>
    /// <returns>The resolved type, or <see langword="null"/> when the type or a parent is missing or the chain is invalid.</returns>
    public static ResolvedType? Resolve(LibrarySet set, TypeId typeId)
    {
        ArgumentNullException.ThrowIfNull(set);
        var run = new Run(set, new LibraryValidationOptions());
        var type = run.FindType(typeId, range: null);
        return type is null ? null : run.ResolveType(type, report: null);
    }

    private sealed class Run(LibrarySet set, LibraryValidationOptions options)
    {
        public List<DocumentProblem> Problems { get; } = [];

        public void Execute()
        {
            Problems.AddRange(set.Primary.LoadProblems);
            CheckDuplicates();
            foreach (var capability in set.Primary.Capabilities)
            {
                CheckCapability(capability);
            }

            foreach (var type in set.Primary.Types)
            {
                CheckType(type);
            }

            CheckSymbolFiles();
            if (set.Primary.Manifest is { } manifest)
            {
                CheckManifest(manifest);
            }
        }

        // ------------------------------------------------------------------ lookup

        public SourceDocument<CapabilityDocument>? FindCapability(CapabilityId id, SemVersionRange? range) =>
            set.All.SelectMany(f => f.Capabilities)
                .Where(c => c.Document.Id == id && (range is null || range.Satisfies(c.Document.Version)))
                .MaxBy(c => c.Document.Version);

        public SourceDocument<DeviceTypeDocument>? FindType(TypeId id, SemVersionRange? range) =>
            set.All.SelectMany(f => f.Types)
                .Where(t => t.Document.Id == id && (range is null || range.Satisfies(t.Document.Version)))
                .MaxBy(t => t.Document.Version);

        /// <summary>Resolves the chain; reports cycle/depth/missing parent problems against <paramref name="report"/> when given.</summary>
        public ResolvedType? ResolveType(SourceDocument<DeviceTypeDocument> type, SourceDocument<DeviceTypeDocument>? report)
        {
            var chain = new List<DeviceTypeDocument> { type.Document };
            var current = type.Document;
            while (current.Extends is { } parent)
            {
                if (chain.Any(t => t.Id == parent.Id))
                {
                    Add(report, "/extends", ProblemCodes.TypeExtendsCycle, ("chain", (IReadOnlyList<string>)[.. chain.Select(t => t.Id.Value), parent.Id.Value]));
                    return null;
                }

                if (chain.Count > MaxExtendsDepth)
                {
                    Add(report, "/extends", ProblemCodes.TypeExtendsTooDeep, ("max", MaxExtendsDepth));
                    return null;
                }

                var next = FindType(parent.Id, parent.Version);
                if (next is null)
                {
                    Add(report, "/extends", ProblemCodes.TypeMissing, ("type", parent.Id.Value), ("version", parent.Version.ToString()));
                    return null;
                }

                chain.Add(next.Document);
                current = next.Document;
            }

            // Parents first; a child's use of a capability replaces its parent's.
            var capabilities = new List<CapabilityUse>();
            var attributes = new List<string>();
            var overrides = new List<StateSummaryOverride>();
            foreach (var doc in Enumerable.Reverse(chain))
            {
                foreach (var use in doc.Capabilities)
                {
                    capabilities.RemoveAll(c => c.Id == use.Id);
                    capabilities.Add(use);
                }

                attributes.AddRange(FieldNames(doc.Attributes).Where(a => !attributes.Contains(a)));
            }

            foreach (var doc in chain)
            {
                overrides.AddRange(doc.StateSummary?.Overrides ?? []);
            }

            var rules = chain.Select(d => d.DefaultAlarmRules ?? []).FirstOrDefault(r => r.Count > 0) ?? [];
            return new ResolvedType(type.Document, capabilities, attributes, type.Document.Display, type.Document.LicenceClass, rules, overrides,
                [.. chain.Select(d => d.Id)]);
        }

        // ------------------------------------------------------------------ capabilities

        private void CheckDuplicates()
        {
            foreach (var group in set.Primary.Capabilities.GroupBy(c => (c.Document.Id, c.Document.Version.ToString())).Where(g => g.Count() > 1))
            {
                foreach (var duplicate in group.Skip(1))
                {
                    Add(duplicate.SourcePath, "/id", ProblemCodes.CapabilityDuplicate, ("id", group.Key.Id.Value), ("version", group.Key.Item2));
                }
            }

            foreach (var group in set.Primary.Types.GroupBy(t => (t.Document.Id, t.Document.Version.ToString())).Where(g => g.Count() > 1))
            {
                foreach (var duplicate in group.Skip(1))
                {
                    Add(duplicate.SourcePath, "/id", ProblemCodes.TypeDuplicate, ("id", group.Key.Id.Value), ("version", group.Key.Item2));
                }
            }
        }

        private void CheckCapability(SourceDocument<CapabilityDocument> source)
        {
            var capability = source.Document;
            for (var i = 0; i < capability.Properties.Count; i++)
            {
                var property = capability.Properties[i];
                var setter = "set" + char.ToUpperInvariant(property.Id.Value[0]) + property.Id.Value[1..];
                if (property.Writable && !capability.Commands.Any(c => c.Id.Value == setter))
                {
                    Add(source.SourcePath, $"/properties/{i}/writable", ProblemCodes.PropertyWritableWithoutSetter,
                        ("property", property.Id.Value), ("command", setter));
                }
            }

            CheckKeys(source.SourcePath, CapabilityKeys(capability));
        }

        private static IEnumerable<string> CapabilityKeys(CapabilityDocument capability)
        {
            var prefix = $"capabilities.{capability.Id}";
            yield return prefix + ".name";
            yield return prefix + ".description";
            foreach (var property in capability.Properties)
            {
                yield return $"{prefix}.properties.{property.Id}.name";
                foreach (var value in property.EnumValues ?? [])
                {
                    yield return $"{prefix}.properties.{property.Id}.values.{value.Code}";
                }
            }

            foreach (var command in capability.Commands)
            {
                yield return $"{prefix}.commands.{command.Id}.name";
                if (command.Risk != "normal" || command.RiskByArgument is { Count: > 0 })
                {
                    yield return $"{prefix}.commands.{command.Id}.confirm";
                }

                foreach (var parameter in FieldNames(command.Parameters))
                {
                    yield return $"{prefix}.commands.{command.Id}.parameters.{parameter}.name";
                }
            }

            foreach (var e in capability.Events)
            {
                yield return $"{prefix}.events.{e.Id}.name";
                yield return $"{prefix}.events.{e.Id}.title";
            }
        }

        // ------------------------------------------------------------------ types

        private void CheckType(SourceDocument<DeviceTypeDocument> source)
        {
            var type = source.Document;
            var used = new Dictionary<CapabilityId, CapabilityDocument>();
            for (var i = 0; i < type.Capabilities.Count; i++)
            {
                var use = type.Capabilities[i];
                var capability = FindCapability(use.Id, use.Version);
                if (capability is null)
                {
                    var any = FindCapability(use.Id, range: null);
                    if (any is null)
                    {
                        Add(source.SourcePath, $"/capabilities/{i}/id", ProblemCodes.CapabilityMissing, ("capability", use.Id.Value));
                    }
                    else
                    {
                        Add(source.SourcePath, $"/capabilities/{i}/version", ProblemCodes.CapabilityVersionUnsatisfied,
                            ("capability", use.Id.Value), ("range", use.Version.ToString()), ("available", any.Document.Version.ToString()));
                    }

                    continue;
                }

                used[use.Id] = capability.Document;
                CheckExclusions(source.SourcePath, $"/capabilities/{i}/exclude", use, capability.Document);
            }

            var resolved = ResolveType(source, source);
            if (resolved is not null)
            {
                CheckResolved(source, resolved);
            }

            var rules = type.DefaultAlarmRules ?? [];
            for (var i = 0; i < rules.Count; i++)
            {
                AddWarning(source.SourcePath, $"/defaultAlarmRules/{i}", ProblemCodes.RuleTemplateUnresolved, ("rule", rules[i]));
            }

            CheckSymbol(source.SourcePath, type.Display.Symbol);
            CheckKeys(source.SourcePath, TypeKeys(type));
        }

        private void CheckResolved(SourceDocument<DeviceTypeDocument> source, ResolvedType resolved)
        {
            var capabilities = resolved.Capabilities
                .Select(u => (Use: u, Doc: FindCapability(u.Id, u.Version)?.Document))
                .Where(c => c.Doc is not null)
                .ToDictionary(c => c.Use.Id.Value, c => (c.Use, Doc: c.Doc!), StringComparer.Ordinal);

            if (!resolved.Capabilities.Any(c => c.Id.Value == "health"))
            {
                Add(source.SourcePath, "/capabilities", ProblemCodes.TypeHealthMissing);
            }

            if (FindProperty(capabilities, resolved.Display.PrimaryProperty) is null)
            {
                Add(source.SourcePath, "/display/primaryProperty", ProblemCodes.TypePrimaryPropertyUnknown, ("property", resolved.Display.PrimaryProperty));
            }

            var overrides = source.Document.StateSummary?.Overrides ?? [];
            for (var i = 0; i < overrides.Count; i++)
            {
                var o = overrides[i];
                var property = FindProperty(capabilities, o.Property);
                if (property is null)
                {
                    Add(source.SourcePath, $"/stateSummary/overrides/{i}/property", ProblemCodes.TypeOverridePropertyUnknown, ("property", o.Property));
                    continue;
                }

                for (var j = 0; j < o.Values.Count; j++)
                {
                    if (!ValueAllowed(property, o.Values[j]))
                    {
                        Add(source.SourcePath, $"/stateSummary/overrides/{i}/values/{j}", ProblemCodes.TypeOverrideValueUnknown,
                            ("property", o.Property), ("value", o.Values[j].ToString()));
                    }
                }
            }

            // Expected effects of the commands this type brings itself (inherited ones were checked with the parent).
            for (var i = 0; i < source.Document.Capabilities.Count; i++)
            {
                var use = source.Document.Capabilities[i];
                if (!capabilities.TryGetValue(use.Id.Value, out var capability))
                {
                    continue;
                }

                foreach (var command in capability.Doc.Commands)
                {
                    if (command.ExpectedEffect is { } effect && !(use.Exclude?.Commands ?? []).Contains(command.Id.Value)
                        && FindProperty(capabilities, effect.Property) is null)
                    {
                        Add(source.SourcePath, $"/capabilities/{i}", ProblemCodes.CommandEffectPropertyUnknown,
                            ("command", $"{use.Id}.{command.Id}"), ("property", effect.Property));
                    }
                }
            }
        }

        private void CheckExclusions(string file, string path, CapabilityUse use, CapabilityDocument capability)
        {
            if (use.Exclude is not { } exclude)
            {
                return;
            }

            var commands = exclude.Commands ?? [];
            for (var i = 0; i < commands.Count; i++)
            {
                if (!capability.Commands.Any(c => c.Id.Value == commands[i]))
                {
                    Add(file, $"{path}/commands/{i}", ProblemCodes.TypeExcludeUnknown, ("member", commands[i]));
                }
            }

            var events = exclude.Events ?? [];
            for (var i = 0; i < events.Count; i++)
            {
                if (!capability.Events.Any(e => e.Id.Value == events[i]))
                {
                    Add(file, $"{path}/events/{i}", ProblemCodes.TypeExcludeUnknown, ("member", events[i]));
                }
            }

            foreach (var (propertyId, codes) in exclude.EnumValues ?? new Dictionary<string, IReadOnlyList<string>>())
            {
                var property = capability.Properties.FirstOrDefault(p => p.Id.Value == propertyId);
                if (property is null)
                {
                    Add(file, $"{path}/enumValues/{propertyId}", ProblemCodes.TypeExcludeUnknown, ("member", propertyId));
                    continue;
                }

                for (var i = 0; i < codes.Count; i++)
                {
                    if (!(property.EnumValues ?? []).Any(v => v.Code.Value == codes[i]))
                    {
                        Add(file, $"{path}/enumValues/{propertyId}/{i}", ProblemCodes.TypeExcludeUnknown, ("member", $"{propertyId}.{codes[i]}"));
                    }
                }
            }
        }

        private static PropertyDefinitionDoc? FindProperty(Dictionary<string, (CapabilityUse Use, CapabilityDocument Doc)> capabilities, string path)
        {
            var dot = path.LastIndexOf('.');
            if (dot <= 0 || !capabilities.TryGetValue(path[..dot], out var capability))
            {
                return null;
            }

            return capability.Doc.Properties.FirstOrDefault(p => p.Id.Value == path[(dot + 1)..]);
        }

        private static bool ValueAllowed(PropertyDefinitionDoc property, JsonElement value) => property.ValueType switch
        {
            "enum" => value.ValueKind == JsonValueKind.String && (property.EnumValues ?? []).Any(v => v.Code.Value == value.GetString()),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" or "decimal" => value.ValueKind == JsonValueKind.Number,
            _ => value.ValueKind == JsonValueKind.String,
        };

        private static IEnumerable<string> TypeKeys(DeviceTypeDocument type)
        {
            var prefix = $"types.{type.Id}";
            yield return prefix + ".name";
            yield return prefix + ".description";
            foreach (var attribute in FieldNames(type.Attributes))
            {
                yield return $"{prefix}.attributes.{attribute}.name";
            }
        }

        // ------------------------------------------------------------------ translations

        private void CheckKeys(string file, IEnumerable<string> keys)
        {
            var list = keys.ToList();
            foreach (var culture in LibraryLoader.Cultures)
            {
                set.Primary.Bundles.TryGetValue(culture, out var bundle);
                foreach (var key in list.Where(k => bundle is null || !HasKey(bundle.Document, k)))
                {
                    var error = culture == "en" || options.StrictI18n;
                    Problems.Add(new DocumentProblem(error ? ProblemSeverity.Error : ProblemSeverity.Warning, file, string.Empty,
                        ErrorCode.FromTrusted(ProblemCodes.I18nKeyMissing), new Dictionary<string, object?> { ["culture"] = culture, ["key"] = key }));
                }
            }
        }

        internal static bool HasKey(JsonElement bundle, string key)
        {
            var node = bundle;
            foreach (var segment in key.Split('.'))
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node))
                {
                    return false;
                }
            }

            return node.ValueKind == JsonValueKind.String && node.GetString()!.Length > 0;
        }

        // ------------------------------------------------------------------ symbols

        private void CheckSymbol(string file, string symbol)
        {
            var states = set.All.Select(f => f.Symbols.GetValueOrDefault(symbol)).FirstOrDefault(s => s is not null);
            foreach (var state in SymbolStates.Where(s => states is null || !states.ContainsKey(s)))
            {
                Add(file, "/display/symbol", ProblemCodes.SymbolMissing, ("symbol", symbol), ("state", state));
            }
        }

        private void CheckSymbolFiles()
        {
            foreach (var (_, states) in set.Primary.Symbols)
            {
                foreach (var (_, path) in states.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    if (SvgSafety.Check(path) is { } reason)
                    {
                        Add(LibraryLoader.Relative(set.Primary.Root, path), string.Empty, ProblemCodes.SymbolUnsafe, ("reason", reason));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ package

        private void CheckManifest(SourceDocument<ManifestDocument> source)
        {
            var manifest = source.Document;
            var root = set.Primary.Root;
            if (manifest.NativeCode && manifest.Isolation != PackageIsolation.IsolatedRequired)
            {
                Add(source.SourcePath, "/isolation", ProblemCodes.ManifestNativeRequiresIsolation);
            }

            var listed = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < manifest.Connectors.Count; i++)
            {
                var assembly = manifest.Connectors[i].Class.Split("::", 2)[0];
                if (!listed.Contains($"lib/{assembly}.dll"))
                {
                    Add(source.SourcePath, $"/connectors/{i}/class", ProblemCodes.ManifestConnectorAssemblyUnlisted, ("assembly", assembly));
                }
            }

            for (var i = 0; i < manifest.Files.Count; i++)
            {
                var file = manifest.Files[i];
                var full = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
                {
                    Add(source.SourcePath, $"/files/{i}", ProblemCodes.ManifestFileMissing, ("file", file.Path));
                    continue;
                }

                using var stream = File.OpenRead(full);
                var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Add(source.SourcePath, $"/files/{i}/sha256", ProblemCodes.ManifestFileHashMismatch, ("file", file.Path), ("actual", actual));
                }
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => LibraryLoader.Relative(root, f))
                .Where(f => f is not ("manifest.json" or "signature.sig") && !listed.Contains(f)).Order(StringComparer.Ordinal))
            {
                Add(source.SourcePath, "/files", ProblemCodes.ManifestFileUnlisted, ("file", file));
            }

            CheckKeys(source.SourcePath, [$"packages.{manifest.Id}.name"]);
        }

        // ------------------------------------------------------------------ helpers

        private static IEnumerable<string> FieldNames(ParameterSchema? schema) =>
            schema is not null && schema.RawElement.ValueKind == JsonValueKind.Object
            && schema.RawElement.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                ? properties.EnumerateObject().Select(p => p.Name)
                : [];

        private void Add(SourceDocument<DeviceTypeDocument>? report, string path, string code, params (string Key, object? Value)[] args)
        {
            if (report is not null)
            {
                Add(report.SourcePath, path, code, args);
            }
        }

        private void Add(string file, string path, string code, params (string Key, object? Value)[] args) =>
            Problems.Add(new DocumentProblem(ProblemSeverity.Error, file, path, ErrorCode.FromTrusted(code), args.ToDictionary(a => a.Key, a => a.Value)));

        private void AddWarning(string file, string path, string code, params (string Key, object? Value)[] args) =>
            Problems.Add(new DocumentProblem(ProblemSeverity.Warning, file, path, ErrorCode.FromTrusted(code), args.ToDictionary(a => a.Key, a => a.Value)));
    }
}
