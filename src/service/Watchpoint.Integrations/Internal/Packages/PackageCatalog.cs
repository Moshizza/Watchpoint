using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>What a scan did.</summary>
/// <param name="Processed">Sources verified (new or changed).</param>
/// <param name="Deferred">Sources skipped because they are still being written; the caller scans again later.</param>
internal readonly record struct ScanOutcome(int Processed, int Deferred);

/// <summary>
/// The package catalogue (spec IRT §4): discovers the packages of the builtin and user folders, verifies them, registers
/// their types with MOD and tracks their state.
/// <para>
/// Consistency: every mutation (scan, install, enable/disable) runs under one gate; each package is verified, cached and
/// registered completely before its entry is published with a single atomic swap of an immutable map. Readers never
/// take the gate and never see a package half-way; a failed load publishes an <see cref="PackageState.Invalid"/> entry
/// (or nothing) and leaves no cache folder, type registration or load context behind.
/// </para>
/// </summary>
internal sealed class PackageCatalog : IPackageCatalog, IDisposable
{
    private const string StagingFolder = ".staging";
    private const string DevKeysFolder = "dev-keys";
    private const string DocsFolder = "docs";
    private const string PackageExtension = ".wpkg";
    private const long MaxUploadBytes = 500L * 1024 * 1024;
    private static readonly SemVersion _unknownVersion = new(0, 0, 0);
    private static readonly System.Buffers.SearchValues<char> _fileNameChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.");

    private readonly IntegrationsOptions _options;
    private readonly PackageVerifier _verifier;
    private readonly ITypeCatalog _types;
    private readonly IStandardLibrary _standardLibrary;
    private readonly IDocumentReader _reader;
    private readonly IPackageStore _store;
    private readonly IPackageConnections _connections;
    private readonly PackageLoadContextPool _pool;
    private readonly TimeProvider _time;
    private readonly ILogger<PackageCatalog> _logger;
    private readonly UnpackLimits _limits;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<(string Id, string Version), PackageRow>? _rows;
    private ImmutableDictionary<string, Entry> _entries = ImmutableDictionary.Create<string, Entry>(StringComparer.Ordinal);
    private long _processed;
    private volatile bool _scanCompleted;

    public PackageCatalog(IntegrationsOptions options, PackageVerifier verifier, ITypeCatalog types, IStandardLibrary standardLibrary,
        IDocumentReader reader, IPackageStore store, IPackageConnections connections, PackageLoadContextPool pool, TimeProvider time,
        ILogger<PackageCatalog> logger, UnpackLimits? limits = null)
    {
        _options = options;
        _verifier = verifier;
        _types = types;
        _standardLibrary = standardLibrary;
        _reader = reader;
        _store = store;
        _connections = connections;
        _pool = pool;
        _time = time;
        _logger = logger;
        _limits = limits ?? UnpackLimits.Default;
    }

    /// <inheritdoc/>
    public event EventHandler<PackageChangedEventArgs>? Changed;

    /// <summary>Gets whether a full scan of both folders has completed (readiness, spec IRT §13).</summary>
    public bool ScanCompleted => _scanCompleted;

    /// <summary>Gets the number of sources verified so far.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processed);

    /// <inheritdoc/>
    public IReadOnlyList<PackageInfo> List() =>
        [.. Volatile.Read(ref _entries).Values.Select(e => e.Info)
            .OrderBy(p => p.Id.Value, StringComparer.Ordinal).ThenBy(p => p.Version).ThenBy(p => p.FilePath, StringComparer.Ordinal)];

    /// <inheritdoc/>
    public Result<PackageInfo> Get(PackageId id, SemVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var entries = Volatile.Read(ref _entries);
        if (entries.TryGetValue(PackageKey(id, version), out var entry))
        {
            return entry.Info;
        }

        var invalid = entries.Values.FirstOrDefault(e => e.Info.Id == id && e.Info.Version == version);
        return invalid is not null ? invalid.Info : IntegrationErrors.NotFound(id, version.ToString());
    }

    /// <inheritdoc/>
    public Result<PackageInfo> GetLatestActive(PackageId id)
    {
        var latest = Volatile.Read(ref _entries).Values
            .Where(e => e.Info.Id == id && e.Info.State == PackageState.Active)
            .MaxBy(e => e.Info.Version);
        return latest is not null ? latest.Info : IntegrationErrors.NotFound(id);
    }

    /// <inheritdoc/>
    public Result<ConnectionSettingsSchema> GetSettingsSchema(PackageId id, SemVersion version, string serverType)
    {
        ArgumentNullException.ThrowIfNull(version);
        return Volatile.Read(ref _entries).TryGetValue(PackageKey(id, version), out var entry) && entry.Schemas.TryGetValue(serverType, out var schema)
            ? schema
            : IntegrationErrors.NotFound(id, version.ToString()).WithArg("serverType", serverType);
    }

    /// <summary>Location and main assembly of an active package version, for the connection host (T1.16).</summary>
    public Result<(string PackageDir, ManifestDocument Manifest)> GetLoadable(PackageId id, SemVersion version)
    {
        if (!Volatile.Read(ref _entries).TryGetValue(PackageKey(id, version), out var entry))
        {
            return IntegrationErrors.NotFound(id, version.ToString());
        }

        return entry is { Info.State: PackageState.Active, CacheDir: not null, Manifest: not null }
            ? (entry.CacheDir, entry.Manifest)
            : IntegrationErrors.NotActive(id, version.ToString());
    }

    /// <summary>The Markdown files of the package's <c>docs/</c> folder, relative to it (API §10 docs index).</summary>
    public IReadOnlyList<string> ListDocumentation(PackageId id, SemVersion version)
    {
        if (!Volatile.Read(ref _entries).TryGetValue(PackageKey(id, version), out var entry) || entry.CacheDir is null)
        {
            return [];
        }

        var docs = Path.Combine(entry.CacheDir, DocsFolder);
        return Directory.Exists(docs)
            ? [.. Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories).Select(f => LibraryLoader.Relative(docs, f)).Order(StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Full path of a documentation file of the package; only Markdown files inside its <c>docs/</c> folder.</summary>
    public Result<string> GetDocumentationFile(PackageId id, SemVersion version, string path)
    {
        if (!Volatile.Read(ref _entries).TryGetValue(PackageKey(id, version), out var entry) || entry.CacheDir is null
            || !PackageUnpacker.IsSafeRelativePath(path) || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return IntegrationErrors.NotFound(id, version.ToString()).WithArg("path", path);
        }

        var docs = Path.Combine(entry.CacheDir, DocsFolder);
        var file = Path.GetFullPath(Path.Combine(docs, path));
        return file.StartsWith(docs + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file)
            ? file
            : IntegrationErrors.NotFound(id, version.ToString()).WithArg("path", path);
    }

    /// <inheritdoc/>
    public async Task<Result<PackageInfo>> InstallAsync(Stream wpkg, string fileName, UserId actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wpkg);
        if (!IsValidUploadName(fileName))
        {
            return IntegrationErrors.FileNameInvalid(fileName ?? string.Empty);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRowsAsync(ct).ConfigureAwait(false);
            Directory.CreateDirectory(_options.PackagesPath);
            var target = Path.Combine(_options.PackagesPath, fileName);

            // Written under a name the watcher ignores, then renamed into place: the scan never sees a partial file.
            var temp = Path.Combine(_options.PackagesPath, $".{fileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await wpkg.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > Math.Min(MaxUploadBytes, _limits.MaxTotalBytes))
                        {
                            return IntegrationErrors.UnsafeArchive("tooLarge");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }

                File.Move(temp, target, overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }

            IntegrationLog.PackageInstalled(_logger, fileName, actor);
            var source = new PackageSource(target, PackageInfo.UserSource, IsFolder: false, Fingerprint.Of(target, isFolder: false));
            var entry = await ProcessAsync(source, ct).ConfigureAwait(false);
            return entry is not null ? entry.Info : IntegrationErrors.UnsafeArchive("unreadable");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<Result> SetEnabledAsync(PackageId id, SemVersion version, bool enabled, UserId actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(PackageKey(id, version), out var entry))
            {
                return _entries.Values.Any(e => e.Info.Id == id && e.Info.Version == version)
                    ? IntegrationErrors.NotActive(id, version.ToString())
                    : IntegrationErrors.NotFound(id, version.ToString());
            }

            var target = enabled ? PackageState.Active : PackageState.Disabled;
            if (entry.Info.State == target)
            {
                return Result.Success();
            }

            if (!enabled)
            {
                // Connections first: once the state is published nothing may start on this version.
                await _connections.StopForPackageAsync(id, version, "packageDisabled", ct).ConfigureAwait(false);
            }

            IntegrationLog.PackageStateSet(_logger, id, version, target, actor);
            await PublishAsync(entry with { Info = entry.Info with { State = target } }, entry.Info.State, ct).ConfigureAwait(false);
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Scans the builtin and user folders: verifies new and changed sources, handles deleted ones.</summary>
    public async Task<ScanOutcome> ScanAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureRowsAsync(ct).ConfigureAwait(false);
            var sources = EnumerateSources(_options.BuiltinPath, PackageInfo.BuiltinSource)
                .Concat(EnumerateSources(_options.PackagesPath, PackageInfo.UserSource)).ToList();
            var processed = 0;
            var deferred = 0;
            foreach (var source in sources)
            {
                ct.ThrowIfCancellationRequested();
                if (_entries.Values.Any(e => !e.FileMissing && PathEquals(e.SourcePath, source.Path) && e.Fingerprint == source.Fingerprint))
                {
                    continue;
                }

                if (await ProcessAsync(source, ct).ConfigureAwait(false) is null)
                {
                    deferred++;
                }
                else
                {
                    processed++;
                }
            }

            foreach (var entry in _entries.Values.Where(e => !sources.Any(s => PathEquals(s.Path, e.SourcePath)) || e.FileMissing).ToList())
            {
                await HandleMissingAsync(entry, ct).ConfigureAwait(false);
            }

            _scanCompleted = true;
            return new ScanOutcome(processed, deferred);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Counts the packages per state (metric <c>watchpoint.integrations.packages</c>).</summary>
    public IReadOnlyDictionary<PackageState, int> CountByState() =>
        Volatile.Read(ref _entries).Values.GroupBy(e => e.Info.State).ToDictionary(g => g.Key, g => g.Count());

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    // ------------------------------------------------------------------ processing (under the gate)

    /// <summary>Verifies one source and publishes its entry; <see langword="null"/> when the source is still being written.</summary>
    private async Task<Entry?> ProcessAsync(PackageSource source, CancellationToken ct)
    {
        var staging = Path.Combine(_options.CachePath, StagingFolder, Guid.NewGuid().ToString("N"));
        string? createdCache = null;
        try
        {
            Result unpacked;
            try
            {
                unpacked = source.IsFolder
                    ? PackageUnpacker.CopyFolder(source.Path, staging, _limits)
                    : PackageUnpacker.Unpack(source.Path, staging, _limits);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked or vanished while reading: a copy in progress. Not a verdict on the package.
                IntegrationLog.PackageSourceBusy(_logger, source.Path);
                return null;
            }

            Interlocked.Increment(ref _processed);
            if (!unpacked.IsSuccess)
            {
                return await PublishInvalidAsync(source, null, PackageProblems.FromError(unpacked.Error, Path.GetFileName(source.Path)), false, ct).ConfigureAwait(false);
            }

            var verification = _verifier.Verify(staging, Dependencies(source.Path), builtin: source.Source == PackageInfo.BuiltinSource);
            if (!verification.IsValid)
            {
                return await PublishInvalidAsync(source, verification.Manifest, verification.Problems, verification.SignedByDevelopmentKey, ct).ConfigureAwait(false);
            }

            var manifest = verification.Manifest!;
            var key = PackageKey(manifest.Id, manifest.Version);
            _entries.TryGetValue(key, out var current);
            if (current is not null && !PathEquals(current.SourcePath, source.Path) && !current.FileMissing)
            {
                // Another file already provides this id and version: the first one wins, whatever this one contains.
                return await PublishInvalidAsync(source, manifest, [Conflict(manifest, current.SourcePath)], verification.SignedByDevelopmentKey, ct).ConfigureAwait(false);
            }

            if (current is not null && current.ManifestSha256 == verification.ManifestSha256 && current.Info.State != PackageState.Invalid)
            {
                // Same content (the manifest lists every file hash): only the file moved or was rewritten.
                var same = current with
                {
                    SourcePath = source.Path,
                    Fingerprint = source.Fingerprint,
                    FileMissing = false,
                    Info = current.Info with { FilePath = source.Path, Source = source.Source, Problems = WithoutCode(current.Info.Problems, IntegrationErrors.PackageFileMissing) },
                };
                await DropOtherEntriesOfSourceAsync(source, key, ct).ConfigureAwait(false);
                await PublishAsync(same, current.Info.State, ct, raise: current.FileMissing).ConfigureAwait(false);
                return same;
            }

            if (current is not null && (_connections.IsInUse(manifest.Id, manifest.Version) || _pool.IsLoaded(manifest.Id, manifest.Version)))
            {
                // Different content under a version connections run on: refused; the loaded version stays as it is.
                var kept = current with
                {
                    Fingerprint = PathEquals(current.SourcePath, source.Path) ? source.Fingerprint : current.Fingerprint,
                    Info = current.Info with { Problems = [.. WithoutCode(current.Info.Problems, IntegrationErrors.PackageVersionConflict), Conflict(manifest, source.Path, ProblemSeverity.Warning)] },
                };
                await PublishAsync(kept, current.Info.State, ct).ConfigureAwait(false);
                return kept;
            }

            // Verified: move the staging folder into the cache, then register the types.
            var cacheDir = Path.Combine(_options.CachePath, manifest.Id.Value, manifest.Version.ToString());
            PackageUnpacker.TryDelete(cacheDir);
            if (Directory.Exists(cacheDir))
            {
                return await PublishInvalidAsync(source, manifest,
                    [PackageProblems.Error(IntegrationErrors.PackageUnreadable, PackageProblems.ManifestFile, ("reason", "the cached copy of this version is in use"))],
                    verification.SignedByDevelopmentKey, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
            Directory.Move(staging, cacheDir);
            createdCache = cacheDir;

            var folder = LibraryLoader.Load(cacheDir, _reader);
            var row = Row(manifest.Id, manifest.Version);
            var discoveredAt = current?.Info.DiscoveredAt ?? row?.DiscoveredAt ?? _time.GetUtcNow();
            IntegrationLog.PackageFound(_logger, manifest.Id, manifest.Version, source.Source);

            var registered = await _types.RegisterPackageAsync(manifest.Id, manifest.Version, Documents(folder), ct).ConfigureAwait(false);
            if (!registered.IsSuccess)
            {
                DeleteCache(cacheDir);
                createdCache = null;
                return await PublishInvalidAsync(source, manifest, PackageProblems.FromError(registered.Error, PackageProblems.ManifestFile), verification.SignedByDevelopmentKey, ct).ConfigureAwait(false);
            }

            var state = row is { Enabled: false } ? PackageState.Disabled : PackageState.Active;
            var entry = new Entry(key, source.Path, source.Fingerprint, false,
                BuildInfo(manifest, state, source, verification.Problems, verification.SignedByDevelopmentKey, discoveredAt, verification.HasLocator),
                manifest, verification.ManifestSha256, cacheDir, folder, verification.Schemas);
            createdCache = null;
            await DropOtherEntriesOfSourceAsync(source, key, ct).ConfigureAwait(false);
            await PublishAsync(entry, current?.Info.State, ct).ConfigureAwait(false);
            if (state == PackageState.Active)
            {
                IntegrationLog.PackageActive(_logger, manifest.Id, manifest.Version);
            }

            if (verification.SignedByDevelopmentKey)
            {
                IntegrationLog.PackageDevelopmentKey(_logger, manifest.Id, manifest.Version);
            }

            foreach (var shadowed in verification.Problems.Where(p => p.Code.Value == IntegrationErrors.PackageSharedAssemblyShadowed))
            {
                IntegrationLog.SharedAssemblyShadowed(_logger, manifest.Id, manifest.Version, shadowed.SourcePath);
            }

            return entry;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unexpected: the package is invalid, nothing of it stays behind.
            if (createdCache is not null)
            {
                DeleteCache(createdCache);
            }

            return await PublishInvalidAsync(source, null,
                [PackageProblems.Error(IntegrationErrors.PackageUnreadable, Path.GetFileName(source.Path), ("reason", ex.Message))], false, ct).ConfigureAwait(false);
        }
        finally
        {
            PackageUnpacker.TryDelete(staging);
        }
    }

    private async Task<Entry> PublishInvalidAsync(PackageSource source, ManifestDocument? manifest, IEnumerable<DocumentProblem> problems, bool developmentKey, CancellationToken ct)
    {
        var key = "src:" + source.Path;
        _entries.TryGetValue(key, out var previous);
        var list = problems.ToList();
        var discoveredAt = previous?.Info.DiscoveredAt ?? _time.GetUtcNow();
        var info = manifest is not null
            ? BuildInfo(manifest, PackageState.Invalid, source, list, developmentKey, discoveredAt, new Dictionary<string, bool>())
            : new PackageInfo(FallbackId(source.Path), _unknownVersion, PackageState.Invalid, string.Empty, string.Empty, source.Source, source.Path,
                [], new LicenceRequirement(string.Empty, null), [], string.Empty, list, developmentKey, discoveredAt);
        var entry = new Entry(key, source.Path, source.Fingerprint, false, info, manifest, null, null, null, _noSchemas);
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            IntegrationLog.PackageInvalid(_logger, info.Id, info.Version, string.Join("; ", list.Where(p => p.Severity == ProblemSeverity.Error).Select(p => p.Code.Value)));
        }
        await DropOtherEntriesOfSourceAsync(source, key, ct).ConfigureAwait(false);
        await PublishAsync(entry, previous?.Info.State, ct).ConfigureAwait(false);
        return entry;
    }

    /// <summary>The source now holds something else: what it provided before is treated as deleted.</summary>
    private async Task DropOtherEntriesOfSourceAsync(PackageSource source, string key, CancellationToken ct)
    {
        foreach (var other in _entries.Values.Where(e => e.Key != key && !e.FileMissing && PathEquals(e.SourcePath, source.Path)).ToList())
        {
            await HandleMissingAsync(other, ct, raiseForInvalid: false).ConfigureAwait(false);
        }
    }

    private async Task HandleMissingAsync(Entry entry, CancellationToken ct, bool raiseForInvalid = true)
    {
        var info = entry.Info;
        var usable = info.State is PackageState.Active or PackageState.Disabled or PackageState.Verified;
        if (usable && (_connections.IsInUse(info.Id, info.Version) || _pool.IsLoaded(info.Id, info.Version)))
        {
            if (!entry.FileMissing)
            {
                IntegrationLog.PackageFileMissing(_logger, info.Id, info.Version, entry.SourcePath);
                var kept = entry with
                {
                    FileMissing = true,
                    Info = info with { Problems = [.. info.Problems, PackageProblems.Warning(IntegrationErrors.PackageFileMissing, PackageProblems.ManifestFile, ("file", entry.SourcePath))] },
                };
                await PublishAsync(kept, info.State, ct).ConfigureAwait(false);
            }

            return;
        }

        // Removed: the types stay registered with MOD (devices keep their definitions, spec IRT §11).
        Volatile.Write(ref _entries, _entries.Remove(entry.Key));
        if (entry.CacheDir is not null)
        {
            DeleteCache(entry.CacheDir);
        }

        var removed = info with { State = PackageState.Removed };
        if (usable)
        {
            IntegrationLog.PackageRemoved(_logger, info.Id, info.Version);
            await SaveAsync(removed, Row(info.Id, info.Version)?.Enabled ?? true, ct).ConfigureAwait(false);
        }

        if (usable || raiseForInvalid)
        {
            Raise(removed, info.State);
        }
    }

    private async Task PublishAsync(Entry entry, PackageState? previous, CancellationToken ct, bool raise = true)
    {
        // The single point where a package becomes visible: one atomic swap of the immutable map.
        Volatile.Write(ref _entries, _entries.SetItem(entry.Key, entry));
        if (entry.Manifest is not null && entry.Info.State != PackageState.Invalid)
        {
            await SaveAsync(entry.Info, entry.Info.State != PackageState.Disabled, ct).ConfigureAwait(false);
        }

        if (raise)
        {
            Raise(entry.Info, previous);
        }
    }

    private async Task SaveAsync(PackageInfo info, bool enabled, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            PackageJson.Write(writer, info.Problems);
        }

        var row = new PackageRow(info.Id.Value, info.Version.ToString(), PackageJson.StateName(info.State), info.Source, info.FilePath,
            Encoding.UTF8.GetString(buffer.ToArray()), info.SignedByDevelopmentKey, enabled, info.DiscoveredAt);
        _rows![(row.Id, row.Version)] = row;
        try
        {
            await _store.SaveAsync(row, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The table mirrors the in-memory state; the row is written again at the next change of this package.
            IntegrationLog.PackageStoreFailed(_logger, ex, info.Id, info.Version);
        }
    }

    private void Raise(PackageInfo info, PackageState? previous)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<PackageChangedEventArgs>>())
        {
            try
            {
                handler(this, new PackageChangedEventArgs(info, previous));
            }
            catch (Exception ex)
            {
                IntegrationLog.ChangedHandlerFailed(_logger, ex);
            }
        }
    }

    private async Task EnsureRowsAsync(CancellationToken ct)
    {
        if (_rows is null)
        {
            var rows = await _store.ListAsync(ct).ConfigureAwait(false);
            _rows = rows.ToDictionary(r => (r.Id, r.Version));
        }
    }

    private PackageRow? Row(PackageId id, SemVersion version) => _rows!.GetValueOrDefault((id.Value, version.ToString()));

    private List<LibraryFolder> Dependencies(string sourcePath) =>
    [
        _standardLibrary.Folder,
        .. _entries.Values
            .Where(e => e.Folder is not null && e.Info.State is PackageState.Active or PackageState.Disabled && !PathEquals(e.SourcePath, sourcePath))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Folder!),
    ];

    // ------------------------------------------------------------------ helpers

    private static readonly IReadOnlyDictionary<string, ConnectionSettingsSchema> _noSchemas = new Dictionary<string, ConnectionSettingsSchema>();

    /// <summary>Deletes a cached copy, and the package's cache folder once its last version is gone.</summary>
    private static void DeleteCache(string cacheDir)
    {
        PackageUnpacker.TryDelete(cacheDir);
        try
        {
            if (Path.GetDirectoryName(cacheDir) is { } parent && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string PackageKey(PackageId id, SemVersion version) => $"pkg:{id.Value}@{version}";

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static DocumentProblem Conflict(ManifestDocument manifest, string otherPath, ProblemSeverity severity = ProblemSeverity.Error) =>
        new(severity, PackageProblems.ManifestFile, string.Empty, ErrorCode.FromTrusted(IntegrationErrors.PackageVersionConflict),
            new Dictionary<string, object?> { ["package"] = manifest.Id.Value, ["version"] = manifest.Version.ToString(), ["other"] = otherPath });

    private static IReadOnlyList<DocumentProblem> WithoutCode(IReadOnlyList<DocumentProblem> problems, string code) =>
        [.. problems.Where(p => p.Code.Value != code)];

    private static PackageId FallbackId(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return PackageId.Parse(name).TryGetValue(out var id, out _) ? id : PackageId.FromTrusted("unknown.package");
    }

    private static bool IsValidUploadName(string? fileName) =>
        fileName is { Length: > PackageExtensionLength and <= 128 }
        && fileName.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase)
        && fileName[0] != '.'
        && !fileName.Contains("..", StringComparison.Ordinal)
        && !fileName.AsSpan().ContainsAnyExcept(_fileNameChars);

    private const int PackageExtensionLength = 5;

    private static PackageInfo BuildInfo(ManifestDocument manifest, PackageState state, PackageSource source, IReadOnlyList<DocumentProblem> problems,
        bool developmentKey, DateTimeOffset discoveredAt, IReadOnlyDictionary<string, bool> hasLocator) =>
        new(manifest.Id, manifest.Version, state, manifest.Vendor, Camel(manifest.Category.ToString()), source.Source, source.Path,
            [.. manifest.Connectors.Select(c => new ConnectorInfo(c.ServerType, c.Features, c.SupportedVendorVersions, hasLocator.GetValueOrDefault(c.ServerType)))],
            new LicenceRequirement(Camel(manifest.Licence.Class.ToString()), manifest.Licence.FeatureKey),
            manifest.Platforms, Camel(manifest.Isolation.ToString()), problems, developmentKey, discoveredAt);

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static PackageTypeDocuments Documents(LibraryFolder folder)
    {
        var translations = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);
        foreach (var (culture, bundle) in folder.Bundles)
        {
            translations[culture] = JsonDocument.Parse(bundle.Document.GetRawText());
        }

        var symbols = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        foreach (var (symbol, states) in folder.Symbols)
        {
            foreach (var (state, path) in states)
            {
                symbols[$"{symbol}/{state}.svg"] = File.ReadAllBytes(path);
            }
        }

        return new PackageTypeDocuments([.. folder.Capabilities.Select(c => c.Document)], [.. folder.Types.Select(t => t.Document)], translations, symbols);
    }

    private IEnumerable<PackageSource> EnumerateSources(string folder, string source)
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IntegrationLog.ScanFailed(_logger, ex);
            yield break;
        }

        foreach (var entry in entries)
        {
            // Dot names are ours (.cache, upload temp files) or partial copies; dev-keys holds keys, not packages.
            if (entry.Name.StartsWith('.') || entry.Name.Equals(DevKeysFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isFolder = entry is DirectoryInfo;
            if (isFolder ? !File.Exists(Path.Combine(entry.FullName, PackageProblems.ManifestFile)) : !entry.Name.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Fingerprint fingerprint;
            try
            {
                fingerprint = Fingerprint.Of(entry.FullName, isFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // vanished meanwhile: the next scan decides
            }

            yield return new PackageSource(entry.FullName, source, isFolder, fingerprint);
        }
    }

    private sealed record PackageSource(string Path, string Source, bool IsFolder, Fingerprint Fingerprint);

    /// <summary>Cheap change detector of a source: size, last write and file count.</summary>
    private readonly record struct Fingerprint(long Length, long LastWriteTicks, int Files)
    {
        public static Fingerprint Of(string path, bool isFolder)
        {
            if (!isFolder)
            {
                var file = new FileInfo(path);
                return new Fingerprint(file.Length, file.LastWriteTimeUtc.Ticks, 1);
            }

            long length = 0;
            long lastWrite = 0;
            var files = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                length += file.Length;
                lastWrite = Math.Max(lastWrite, file.LastWriteTimeUtc.Ticks);
                files++;
            }

            return new Fingerprint(length, lastWrite, files);
        }
    }

    private sealed record Entry(string Key, string SourcePath, Fingerprint Fingerprint, bool FileMissing, PackageInfo Info, ManifestDocument? Manifest,
        string? ManifestSha256, string? CacheDir, LibraryFolder? Folder, IReadOnlyDictionary<string, ConnectionSettingsSchema> Schemas);
}
