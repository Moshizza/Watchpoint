using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Internal.Storage;

namespace Watchpoint.Model.Internal;

/// <summary>
/// Immutable snapshot of type catalogue state.
/// </summary>
internal sealed class CatalogSnapshot
{
    public long Revision { get; init; }

    public IReadOnlyDictionary<TypeRef, DeviceTypeDefinition> Types { get; init; } = new Dictionary<TypeRef, DeviceTypeDefinition>();

    public IReadOnlyDictionary<(TypeId, int), DeviceTypeDefinition> LatestTypesByMajor { get; init; } = new Dictionary<(TypeId, int), DeviceTypeDefinition>();

    public IReadOnlyDictionary<(CapabilityId, SemVersion), CapabilityDefinition> Capabilities { get; init; } = new Dictionary<(CapabilityId, SemVersion), CapabilityDefinition>();

    public IReadOnlyDictionary<PackageId, RegisteredPackage> RegisteredPackages { get; init; } = new Dictionary<PackageId, RegisteredPackage>();

    public IReadOnlyDictionary<string, JsonElement> StandardBundles { get; init; } = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Symbols { get; init; } = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

    public TypeResources Resources { get; init; } = null!;
}

internal sealed record RegisteredPackage(
    PackageId Id,
    SemVersion Version,
    IReadOnlyDictionary<string, string> Hashes,
    PackageTypeDocuments Documents);

/// <summary>
/// Type catalog implementation using immutable snapshots and atomic swap (spec MOD §5.1).
/// </summary>
internal sealed class TypeCatalog : ITypeCatalog, IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new SemVersionJsonConverter());
        options.Converters.Add(new SemVersionRangeJsonConverter());
        return options;
    }

    private sealed class SemVersionJsonConverter : System.Text.Json.Serialization.JsonConverter<SemVersion>
    {
        public override SemVersion? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            SemVersion.Parse(reader.GetString()).Value;

        public override void Write(Utf8JsonWriter writer, SemVersion value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    private sealed class SemVersionRangeJsonConverter : System.Text.Json.Serialization.JsonConverter<SemVersionRange>
    {
        public override SemVersionRange? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            SemVersionRange.Parse(reader.GetString()).Value;

        public override void Write(Utf8JsonWriter writer, SemVersionRange value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
    private readonly IDbContextFactory<ModelDbContext>? _dbFactory;
    private readonly IDocumentReader _reader;
    private readonly ILogger<TypeCatalog> _logger;
    private readonly TimeProvider _clock;
    private readonly ModelMetrics _metrics;
    private CatalogSnapshot _snapshot;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public TypeCatalog(
        LoadedLibrary standardLibrary,
        IDbContextFactory<ModelDbContext>? dbFactory,
        IDocumentReader reader,
        ILogger<TypeCatalog> logger,
        TimeProvider clock,
        ModelMetrics metrics)
    {
        _dbFactory = dbFactory;
        _reader = reader;
        _logger = logger;
        _clock = clock;
        _metrics = metrics;

        var start = _clock.GetTimestamp();
        _snapshot = BuildInitialSnapshot(standardLibrary);
        var elapsed = _clock.GetElapsedTime(start);

        ModelLog.StandardLibraryLoaded(_logger, _snapshot.Capabilities.Count, _snapshot.Types.Count, elapsed.TotalMilliseconds);
        UpdateMetrics();
    }

    public void Dispose()
    {
        _mutationLock.Dispose();
    }

    public long Revision => Volatile.Read(ref _snapshot).Revision;

    public event EventHandler<CatalogChangedEventArgs>? Changed;

    public Result<DeviceTypeDefinition> Get(TypeRef type)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Types.TryGetValue(type, out var def))
        {
            return def;
        }

        if (_dbFactory is not null)
        {
            try
            {
                using var db = _dbFactory.CreateDbContext();
                var record = db.Documents
                    .AsNoTracking()
                    .FirstOrDefault(d => d.Kind == "type" && d.Id == type.Id.Value && d.Version == type.Version.ToString());

                if (record is not null)
                {
                    var readRes = _reader.ReadDeviceType(Encoding.UTF8.GetBytes(record.Content), record.Id);
                    if (readRes.TryGetValue(out var doc, out var readErr))
                    {
                        var sourceDesc = $"{record.Source}@{record.SourceVersion} (removed)";
                        var resolved = TypeResolver.Resolve(
                            doc,
                            sourceDesc,
                            (id, ver) => LookupTypeDoc(id, ver, db),
                            (id, ver) => LookupCapDef(id, ver, db));

                        if (resolved.TryGetValue(out var historicalDef, out var resolveErr))
                        {
                            return historicalDef;
                        }

                        ModelLog.DocumentWarning(_logger, "historical_type_resolve_error", type.Id.Value, resolveErr.MessageKey);
                    }
                    else
                    {
                        var fields = string.Join(", ", readErr.FieldErrors.Select(f => $"{f.Path}: {f.Code.Value}"));
                        ModelLog.DocumentWarning(_logger, "historical_type_read_error", type.Id.Value, $"{readErr.MessageKey}: {fields} -- json: {record.Content}");
                    }
                }
                else
                {
                    ModelLog.DocumentWarning(_logger, "historical_type_not_in_db", type.Id.Value, type.Version.ToString());
                }
            }
            catch (Exception ex)
            {
                ModelLog.DocumentWarning(_logger, "historical_resolution_failed", type.Id.Value, ex.Message);
            }
        }

        return ModelErrors.TypeNotFoundError(type.Id, type.Version);
    }

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.LatestTypesByMajor.TryGetValue((id, major), out var def))
        {
            return def;
        }

        return ModelErrors.TypeNotFoundMajorError(id, major);
    }

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        IEnumerable<DeviceTypeDefinition> items = snapshot.Types.Values;

        if (query.Role.HasValue)
        {
            items = items.Where(t => t.Role == query.Role.Value);
        }

        if (query.Category.HasValue)
        {
            items = items.Where(t => t.Category == query.Category.Value);
        }

        if (!string.IsNullOrEmpty(query.Source))
        {
            items = items.Where(t => string.Equals(t.Source, query.Source, StringComparison.Ordinal));
        }

        if (query.Capability.HasValue)
        {
            items = items.Where(t => t.Has(query.Capability.Value));
        }

        return items.OrderBy(t => t.Ref.Id.Value, StringComparer.Ordinal).ThenBy(t => t.Ref.Version).ToList();
    }

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot.Capabilities.TryGetValue((id, version), out var cap))
        {
            return cap;
        }

        return ModelErrors.CapabilityNotFoundError(id, version);
    }

    public async Task<Result<CatalogChange>> RegisterPackageAsync(
        PackageId package,
        SemVersion packageVersion,
        PackageTypeDocuments documents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);

        foreach (var typeDoc in documents.Types)
        {
            if (!typeDoc.Id.Value.StartsWith("vendor.", StringComparison.Ordinal))
            {
                return ModelErrors.ReservedNamespaceError(typeDoc.Id.Value);
            }
        }

        foreach (var capDoc in documents.Capabilities)
        {
            if (!capDoc.Id.Value.StartsWith("vendor.", StringComparison.Ordinal))
            {
                return ModelErrors.ReservedNamespaceError(capDoc.Id.Value);
            }
        }

        var newHashes = new Dictionary<string, (string Sha, string Json)>(StringComparer.Ordinal);
        foreach (var cap in documents.Capabilities)
        {
            var json = SerializeDoc(cap);
            var sha = HashSha256(json);
            newHashes[$"capability:{cap.Id.Value}:{cap.Version}"] = (sha, json);
        }

        foreach (var t in documents.Types)
        {
            var json = SerializeDoc(t);
            var sha = HashSha256(json);
            newHashes[$"type:{t.Id.Value}:{t.Version}"] = (sha, json);
        }

        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentSnapshot = Volatile.Read(ref _snapshot);

            if (currentSnapshot.RegisteredPackages.TryGetValue(package, out var existingPkg)
                && existingPkg.Version == packageVersion)
            {
                var sameContent = existingPkg.Hashes.Count == newHashes.Count
                    && newHashes.All(h => existingPkg.Hashes.TryGetValue(h.Key, out var existingSha) && existingSha == h.Value.Sha);

                if (sameContent)
                {
                    return new CatalogChange([], []);
                }

                return ModelErrors.ConflictError("Package version re-registered with different content", package.Value, packageVersion.ToString());
            }

            foreach (var (key, (sha, _)) in newHashes)
            {
                var parts = key.Split(':');
                var kind = parts[0];
                var id = parts[1];
                var ver = parts[2];

                if (kind == "type")
                {
                    var tId = TypeId.FromTrusted(id);
                    var semVer = SemVersion.Parse(ver).Value;
                    if (currentSnapshot.Types.TryGetValue(new TypeRef(tId, semVer), out var existingType))
                    {
                        if (existingType.Source != package.Value)
                        {
                            return ModelErrors.ConflictError("Type already registered by another source", id, ver);
                        }
                    }
                }
                else if (kind == "capability")
                {
                    var cId = CapabilityId.FromTrusted(id);
                    var semVer = SemVersion.Parse(ver).Value;
                    if (currentSnapshot.Capabilities.ContainsKey((cId, semVer)))
                    {
                        return ModelErrors.ConflictError("Capability already registered by another source", id, ver);
                    }
                }
            }

            var newCapMap = new Dictionary<(CapabilityId, SemVersion), CapabilityDefinition>(currentSnapshot.Capabilities);
            var addedCaps = new List<CapabilityId>();
            foreach (var capDoc in documents.Capabilities)
            {
                var capDef = ToCapabilityDefinition(capDoc);
                newCapMap[(capDoc.Id, capDoc.Version)] = capDef;
                addedCaps.Add(capDoc.Id);
            }

            var newTypeMap = new Dictionary<TypeRef, DeviceTypeDefinition>(currentSnapshot.Types);
            var addedTypes = new List<TypeRef>();

            Result<DeviceTypeDocument> TypeLookup(TypeId id, SemVersionRange range)
            {
                var candidate = documents.Types
                    .Where(d => d.Id == id && range.Satisfies(d.Version))
                    .OrderByDescending(d => d.Version)
                    .FirstOrDefault();

                if (candidate is not null)
                {
                    return candidate;
                }

                var existing = currentSnapshot.Types.Values
                    .Where(t => t.Ref.Id == id && range.Satisfies(t.Ref.Version))
                    .OrderByDescending(t => t.Ref.Version)
                    .FirstOrDefault();

                if (existing is not null)
                {
                    return ToDeviceTypeDocument(existing);
                }

                return Error.NotFound(ModelErrors.TypeNotFound.Value, ("typeId", id.Value), ("range", range.ToString()));
            }

            Result<CapabilityDefinition> CapLookup(CapabilityId id, SemVersionRange range)
            {
                var cap = newCapMap.Where(c => c.Key.Item1 == id && range.Satisfies(c.Key.Item2))
                    .OrderByDescending(c => c.Key.Item2)
                    .Select(c => c.Value)
                    .FirstOrDefault();

                if (cap is not null)
                {
                    return cap;
                }

                return Error.NotFound(ModelErrors.CapabilityNotFound.Value, ("capabilityId", id.Value), ("range", range.ToString()));
            }

            foreach (var typeDoc in documents.Types)
            {
                var resolved = TypeResolver.Resolve(typeDoc, package.Value, TypeLookup, CapLookup);
                if (!resolved.TryGetValue(out var def, out var err))
                {
                    return err;
                }

                newTypeMap[def.Ref] = def;
                addedTypes.Add(def.Ref);
            }

            var newLatest = ComputeLatest(newTypeMap.Values);

            var pkgBundles = new Dictionary<string, IReadOnlyList<JsonDocument>>(StringComparer.Ordinal);
            foreach (var (culture, doc) in documents.Translations)
            {
                pkgBundles[culture] = [doc];
            }

            var mergedSymbols = new Dictionary<string, ReadOnlyMemory<byte>>(currentSnapshot.Symbols, StringComparer.Ordinal);
            foreach (var (symKey, bytes) in documents.Symbols)
            {
                mergedSymbols[symKey] = bytes;
            }

            var newResources = TypeResources.Build(currentSnapshot.StandardBundles, pkgBundles, mergedSymbols);

            var newRegisteredPackages = new Dictionary<PackageId, RegisteredPackage>(currentSnapshot.RegisteredPackages)
            {
                [package] = new RegisteredPackage(
                    package,
                    packageVersion,
                    newHashes.ToDictionary(k => k.Key, k => k.Value.Sha, StringComparer.Ordinal),
                    documents),
            };

            var newRevision = currentSnapshot.Revision + 1;
            var newSnapshot = new CatalogSnapshot
            {
                Revision = newRevision,
                Types = newTypeMap,
                LatestTypesByMajor = newLatest,
                Capabilities = newCapMap,
                RegisteredPackages = newRegisteredPackages,
                StandardBundles = currentSnapshot.StandardBundles,
                Symbols = mergedSymbols,
                Resources = newResources,
            };

            if (_dbFactory is not null)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var now = _clock.GetUtcNow();

                foreach (var (key, (sha, json)) in newHashes)
                {
                    var parts = key.Split(':');
                    var kind = parts[0];
                    var id = parts[1];
                    var ver = parts[2];

                    var existingRecord = await db.Documents.FirstOrDefaultAsync(
                        d => d.Kind == kind && d.Id == id && d.Version == ver, ct).ConfigureAwait(false);

                    if (existingRecord is null)
                    {
                        db.Documents.Add(new DocumentRecord
                        {
                            Kind = kind,
                            Id = id,
                            Version = ver,
                            Source = package.Value,
                            SourceVersion = packageVersion.ToString(),
                            Content = json,
                            Sha256 = sha,
                            RegisteredAt = now,
                            RemovedAt = null,
                        });
                    }
                    else
                    {
                        existingRecord.RemovedAt = null;
                    }
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _snapshot, newSnapshot);
            UpdateMetrics();

            var versionStr = packageVersion.ToString();
            ModelLog.PackageRegistered(_logger, package, versionStr, documents.Types.Count, documents.Capabilities.Count);

            var change = new CatalogChange(addedTypes, addedCaps);
            Changed?.Invoke(this, new CatalogChangedEventArgs(change, newRevision));

            return change;
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public async Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct)
    {
        await _mutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var currentSnapshot = Volatile.Read(ref _snapshot);
            if (!currentSnapshot.RegisteredPackages.TryGetValue(package, out var pkg) || pkg.Version != packageVersion)
            {
                return Result.Success();
            }

            var newTypes = currentSnapshot.Types.Where(t => t.Value.Source != package.Value).ToDictionary(t => t.Key, t => t.Value);
            var newLatest = ComputeLatest(newTypes.Values);
            var newCaps = currentSnapshot.Capabilities.Where(c => !c.Key.Item1.Value.StartsWith($"vendor.{package.Value}", StringComparison.Ordinal)
                && !pkg.Documents.Capabilities.Any(d => d.Id == c.Key.Item1 && d.Version == c.Key.Item2)).ToDictionary(c => c.Key, c => c.Value);

            var newPackages = new Dictionary<PackageId, RegisteredPackage>(currentSnapshot.RegisteredPackages);
            newPackages.Remove(package);

            var newResources = TypeResources.Build(currentSnapshot.StandardBundles, new Dictionary<string, IReadOnlyList<JsonDocument>>(), currentSnapshot.Symbols);

            var newRevision = currentSnapshot.Revision + 1;
            var newSnapshot = new CatalogSnapshot
            {
                Revision = newRevision,
                Types = newTypes,
                LatestTypesByMajor = newLatest,
                Capabilities = newCaps,
                RegisteredPackages = newPackages,
                StandardBundles = currentSnapshot.StandardBundles,
                Symbols = currentSnapshot.Symbols,
                Resources = newResources,
            };

            if (_dbFactory is not null)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var now = _clock.GetUtcNow();
                var records = await db.Documents
                    .Where(d => d.Source == package.Value && d.SourceVersion == packageVersion.ToString() && d.RemovedAt == null)
                    .ToListAsync(ct).ConfigureAwait(false);

                foreach (var rec in records)
                {
                    rec.RemovedAt = now;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _snapshot, newSnapshot);
            UpdateMetrics();

            var versionStr = packageVersion.ToString();
            ModelLog.PackageUnregistered(_logger, package, versionStr);
            Changed?.Invoke(this, new CatalogChangedEventArgs(new CatalogChange([], []), newRevision));

            return Result.Success();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    public ITypeResources Resources => Volatile.Read(ref _snapshot).Resources;

    private static CatalogSnapshot BuildInitialSnapshot(LoadedLibrary standardLibrary)
    {
        var capabilities = new Dictionary<(CapabilityId, SemVersion), CapabilityDefinition>();
        foreach (var capDoc in standardLibrary.Capabilities)
        {
            var def = ToCapabilityDefinition(capDoc.Document);
            capabilities[(capDoc.Document.Id, capDoc.Document.Version)] = def;
        }

        var types = new Dictionary<TypeRef, DeviceTypeDefinition>();
        var standardTypes = standardLibrary.Types.Select(t => t.Document).ToList();

        Result<DeviceTypeDocument> TypeLookup(TypeId id, SemVersionRange range)
        {
            var candidate = standardTypes
                .Where(t => t.Id == id && range.Satisfies(t.Version))
                .OrderByDescending(t => t.Version)
                .FirstOrDefault();

            if (candidate is not null)
            {
                return candidate;
            }

            return Error.NotFound(ModelErrors.TypeNotFound.Value, ("typeId", id.Value), ("range", range.ToString()));
        }

        Result<CapabilityDefinition> CapLookup(CapabilityId id, SemVersionRange range)
        {
            var cap = capabilities.Where(c => c.Key.Item1 == id && range.Satisfies(c.Key.Item2))
                .OrderByDescending(c => c.Key.Item2)
                .Select(c => c.Value)
                .FirstOrDefault();

            if (cap is not null)
            {
                return cap;
            }

            return Error.NotFound(ModelErrors.CapabilityNotFound.Value, ("capabilityId", id.Value), ("range", range.ToString()));
        }

        foreach (var typeDoc in standardTypes)
        {
            var resolved = TypeResolver.Resolve(typeDoc, "standard", TypeLookup, CapLookup);
            if (resolved.TryGetValue(out var def, out var err))
            {
                types[def.Ref] = def;
            }
            else
            {
                throw new InvalidOperationException($"Standard library failed to resolve type {typeDoc.Id}: {err.MessageKey}");
            }
        }

        var latest = ComputeLatest(types.Values);
        var stdBundles = standardLibrary.Bundles.ToDictionary(b => b.Key, b => b.Value.Document, StringComparer.Ordinal);
        var resources = TypeResources.Build(stdBundles, new Dictionary<string, IReadOnlyList<JsonDocument>>(), standardLibrary.Symbols);

        return new CatalogSnapshot
        {
            Revision = 1,
            Types = types,
            LatestTypesByMajor = latest,
            Capabilities = capabilities,
            RegisteredPackages = new Dictionary<PackageId, RegisteredPackage>(),
            StandardBundles = stdBundles,
            Symbols = standardLibrary.Symbols,
            Resources = resources,
        };
    }

    private static Dictionary<(TypeId, int), DeviceTypeDefinition> ComputeLatest(IEnumerable<DeviceTypeDefinition> types)
    {
        var latest = new Dictionary<(TypeId, int), DeviceTypeDefinition>();
        foreach (var t in types)
        {
            var key = (t.Ref.Id, t.Ref.Version.Major);
            if (!latest.TryGetValue(key, out var current) || t.Ref.Version > current.Ref.Version)
            {
                latest[key] = t;
            }
        }

        return latest;
    }

    private void UpdateMetrics()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        _metrics.Revision = snapshot.Revision;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in snapshot.Types.Values)
        {
            counts[t.Source] = counts.TryGetValue(t.Source, out var c) ? c + 1 : 1;
        }

        _metrics.TypeCounts = counts;
    }

    private Result<DeviceTypeDocument> LookupTypeDoc(TypeId id, SemVersionRange range, ModelDbContext db)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var snapshotMatch = snapshot.Types.Values
            .Where(t => t.Ref.Id == id && range.Satisfies(t.Ref.Version))
            .OrderByDescending(t => t.Ref.Version)
            .FirstOrDefault();

        if (snapshotMatch is not null)
        {
            return ToDeviceTypeDocument(snapshotMatch);
        }

        var records = db.Documents
            .AsNoTracking()
            .Where(d => d.Kind == "type" && d.Id == id.Value)
            .ToList();

        var matching = records
            .Select(r => _reader.ReadDeviceType(Encoding.UTF8.GetBytes(r.Content), r.Id))
            .Where(r => r.IsSuccess && range.Satisfies(r.Value.Version))
            .Select(r => r.Value)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

        if (matching is not null)
        {
            return matching;
        }

        return Error.NotFound(ModelErrors.TypeNotFound.Value, ("typeId", id.Value), ("range", range.ToString()));
    }

    private Result<CapabilityDefinition> LookupCapDef(CapabilityId id, SemVersionRange range, ModelDbContext db)
    {
        // First check in current snapshot (which includes standard library capabilities)
        var snapshot = Volatile.Read(ref _snapshot);
        var snapshotMatch = snapshot.Capabilities
            .Where(c => c.Key.Item1 == id && range.Satisfies(c.Key.Item2))
            .OrderByDescending(c => c.Key.Item2)
            .Select(c => c.Value)
            .FirstOrDefault();

        if (snapshotMatch is not null)
        {
            return snapshotMatch;
        }

        var records = db.Documents
            .AsNoTracking()
            .Where(d => d.Kind == "capability" && d.Id == id.Value)
            .ToList();

        var matching = records
            .Select(r => _reader.ReadCapability(Encoding.UTF8.GetBytes(r.Content), r.Id))
            .Where(r => r.IsSuccess && range.Satisfies(r.Value.Version))
            .Select(r => ToCapabilityDefinition(r.Value))
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();

        if (matching is not null)
        {
            return matching;
        }

        return Error.NotFound(ModelErrors.CapabilityNotFound.Value, ("capabilityId", id.Value), ("range", range.ToString()));
    }

    private static CapabilityDefinition ToCapabilityDefinition(CapabilityDocument doc)
    {
        var props = new Dictionary<MemberId, PropertyDefinition>();
        foreach (var p in doc.Properties)
        {
            var propPath = new PropertyPath(doc.Id, p.Id);
            var enumVals = p.EnumValues?.Select(e => new EnumValueDefinition(e.Code.Value, ParseSeverity(e.Severity))).ToList() ?? [];
            var numRange = p.Range is null ? null : new NumericRange(p.Range.Min, p.Range.Max);
            var history = p.History is null ? new HistoryPolicy("all") : new HistoryPolicy(p.History.Mode, p.History.Type, p.History.Threshold, p.History.Interval);
            var boolSev = p.BooleanSeverity is null ? null : new BooleanSeverity(ParseSeverity(p.BooleanSeverity.True), ParseSeverity(p.BooleanSeverity.False));
            var defaultVal = p.Default.HasValue ? ParseJsonElementToWpValue(p.Default.Value) : (WpValue?)null;

            props[p.Id] = new PropertyDefinition(
                propPath,
                p.ValueType,
                enumVals,
                p.Unit,
                numRange,
                p.Decimals,
                p.MaxLength,
                p.ItemType,
                p.ObjectSchema,
                p.Writable,
                history,
                p.StaleAfter,
                defaultVal,
                boolSev);
        }

        var cmds = new Dictionary<MemberId, CommandDefinition>();
        foreach (var c in doc.Commands)
        {
            var cmdPath = new CommandPath(doc.Id, c.Id);
            var risk = ParseRisk(c.Risk);
            var timeout = c.Timeout ?? TimeSpan.FromSeconds(10);
            var riskByArg = new Dictionary<string, IReadOnlyDictionary<string, Risk>>(StringComparer.Ordinal);
            if (c.RiskByArgument is not null)
            {
                foreach (var (argName, overrides) in c.RiskByArgument)
                {
                    riskByArg[argName] = overrides.ToDictionary(o => o.Key, o => ParseRisk(o.Value), StringComparer.Ordinal);
                }
            }

            ExpectedEffect? expected = null;
            if (c.ExpectedEffect is not null)
            {
                var effectProp = PropertyPath.FromTrusted(c.ExpectedEffect.Property);
                var eqVal = c.ExpectedEffect.EqualsValue.HasValue ? ParseJsonElementToWpValue(c.ExpectedEffect.EqualsValue.Value) : (WpValue?)null;
                var inVals = c.ExpectedEffect.In?.Select(ParseJsonElementToWpValue).ToList();
                expected = new ExpectedEffect(effectProp, eqVal, inVals, c.ExpectedEffect.Within);
            }

            cmds[c.Id] = new CommandDefinition(
                cmdPath,
                c.Parameters,
                risk,
                riskByArg,
                timeout,
                c.Idempotent,
                c.Batchable,
                expected);
        }

        var evts = new Dictionary<MemberId, EventDefinition>();
        foreach (var e in doc.Events)
        {
            var evtPath = new EventPath(doc.Id, e.Id);
            var sev = ParseSeverity(e.Severity);
            evts[e.Id] = new EventDefinition(evtPath, sev, e.Payload ?? new ParameterSchema(JsonDocument.Parse("{}").RootElement), e.PersonalData ?? []);
        }

        return new CapabilityDefinition(doc.Id, doc.Version, props, cmds, evts);
    }

    private static DeviceTypeDocument ToDeviceTypeDocument(DeviceTypeDefinition def)
    {
        var capUses = def.Capabilities.Select(c => new CapabilityUse(c.Key, SemVersionRange.Parse($"^{c.Value.Version}").Value, c.Value.Optional)).ToList();
        var ovs = def.StateOverrides.Select(o => new StateSummaryOverride(
            o.Property.Value,
            o.Values.Select(v => JsonDocument.Parse(v.Kind == WpValueKind.String ? $"\"{v.AsString()}\"" : v.ToString()).RootElement.Clone()).ToList(),
            o.State.ToString())).ToList();

        return new DeviceTypeDocument(
            def.Ref.Id,
            def.Ref.Version,
            def.Role,
            def.Category,
            def.Lineage.Count > 1 ? new TypeReference(def.Lineage[1].Id, SemVersionRange.Parse($"^{def.Lineage[1].Version}").Value) : null,
            capUses,
            def.Attributes,
            def.AllowedChildTypes,
            def.LicenceClass,
            def.Display,
            new StateSummaryRules(ovs),
            def.DefaultAlarmRules,
            def.DefaultLinks);
    }

    private static Severity ParseSeverity(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "unknown" => Severity.Unknown,
            "normal" => Severity.Normal,
            "info" => Severity.Normal,
            "normalactive" => Severity.NormalActive,
            "warning" => Severity.Warning,
            "offline" => Severity.Offline,
            "fault" => Severity.Fault,
            "alarm" => Severity.Alarm,
            _ => Severity.Unknown,
        };
    }

    private static Risk ParseRisk(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "sensitive" => Risk.Sensitive,
            "critical" => Risk.Critical,
            _ => Risk.Normal,
        };
    }

    private static WpValue ParseJsonElementToWpValue(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.True => WpValue.Bool(true),
            JsonValueKind.False => WpValue.Bool(false),
            JsonValueKind.Number when elem.TryGetInt64(out var l) => WpValue.Int(l),
            JsonValueKind.Number when elem.TryGetDecimal(out var d) => WpValue.Decimal(d),
            JsonValueKind.String => WpValue.Enum(elem.GetString()!),
            _ => WpValue.Null,
        };
    }

    private static string SerializeDoc<T>(T doc)
    {
        var json = JsonSerializer.Serialize(doc, _jsonOptions);
        using var jsonDoc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (doc is DeviceTypeDocument)
            {
                writer.WriteString("$schema", "https://schemas.watchpoint.dev/device-type/1.json");
                // Ensure extends is written as null if null
                if (!jsonDoc.RootElement.TryGetProperty("extends", out _))
                {
                    writer.WriteNull("extends");
                }
            }
            else if (doc is CapabilityDocument)
            {
                writer.WriteString("$schema", "https://schemas.watchpoint.dev/capability/1.json");
            }

            foreach (var prop in jsonDoc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string HashSha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
