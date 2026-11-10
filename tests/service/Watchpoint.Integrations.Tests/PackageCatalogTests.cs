using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Tests.Support;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;
using Watchpoint.Testing.Async;

namespace Watchpoint.Integrations.Tests;

/// <summary>Package discovery, catalogue states, activation and the catalogue API (spec IRT §4.1, §4.2, §4.5).</summary>
[Trait("layer", "U")]
[Trait("spec", "IRT")]
public sealed class PackageCatalogTests
{
    private static readonly PackageId _acme = PackageId.FromTrusted("vendor.acme");
    private static readonly SemVersion _v1 = new(1, 0, 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IRT_U_001_Scan_finds_builtin_and_user_packages()
    {
        await using var h = new RuntimeHarness();
        var builtin = new TestPackage("vendor.builtin").WithConnector().WriteFolder(h.BuiltinFile("vendor.builtin"), h.ReleaseKey);
        var user = new TestPackage("vendor.user").WithConnector().WriteWpkg(h.UserFile("vendor.user-1.0.0.wpkg"), h.ReleaseKey);
        File.WriteAllText(h.UserFile("notes.txt"), "not a package");
        Directory.CreateDirectory(h.UserFile("not-a-package"));
        h.Catalog.ScanCompleted.ShouldBeFalse();

        var outcome = await h.Catalog.ScanAsync(Ct);

        outcome.ShouldBe(new Internal.Packages.ScanOutcome(Processed: 2, Deferred: 0));
        h.Catalog.ScanCompleted.ShouldBeTrue();
        h.Catalog.List().Select(p => (p.Id.Value, p.Source, p.FilePath, p.State)).ShouldBe(
        [
            ("vendor.builtin", PackageInfo.BuiltinSource, builtin, PackageState.Active),
            ("vendor.user", PackageInfo.UserSource, user, PackageState.Active),
        ]);
        h.Logs.WithEventId(1801).Count().ShouldBe(2);

        // A second scan of unchanged folders verifies nothing again.
        (await h.Catalog.ScanAsync(Ct)).ShouldBe(new Internal.Packages.ScanOutcome(0, 0));
        h.Catalog.ProcessedCount.ShouldBe(2);
    }

    [Fact]
    public async Task IRT_U_002_Watcher_picks_up_a_new_file_debounced()
    {
        var wpkg = new TestPackage().WithConnector();

        // --- Real FileSystemWatcher: partial copy under a temporary name, rename into place, locked file.
        await using (var h = new RuntimeHarness())
        {
            h.Options.RescanInterval = TimeSpan.FromDays(1); // only notifications can trigger a scan here
            var bytes = wpkg.ToWpkg(h.ReleaseKey);
            var watcher = h.Watcher();
            await watcher.StartAsync(Ct);
            watcher.ScansCompleted.ShouldBe(1);

            // A copy in progress under a temporary name is not a package, however long it takes.
            var partial = h.UserFile("vendor.acme-1.0.0.wpkg.part");
            File.WriteAllBytes(partial, bytes[..(bytes.Length / 2)]);
            await Eventually.UntilAsync(() => watcher.Notifications > 0, because: "the watcher sees the partial file", ct: Ct);
            await AdvanceUntilAsync(h, () => watcher.ScansCompleted >= 2, "the folder is scanned after the debounce time");
            h.Catalog.List().ShouldBeEmpty();
            h.Catalog.ProcessedCount.ShouldBe(0);

            // The copy completes and is renamed into place: found, but not before the folder was quiet for 2 s.
            File.WriteAllBytes(partial, bytes);
            var seen = watcher.Notifications;
            var scans = watcher.ScansCompleted;
            File.Move(partial, h.UserFile("vendor.acme-1.0.0.wpkg"));
            await Eventually.UntilAsync(() => watcher.Notifications > seen, because: "the watcher sees the rename", ct: Ct);
            h.Clock.Advance(h.Options.Debounce - TimeSpan.FromMilliseconds(1));
            watcher.ScansCompleted.ShouldBe(scans, "no scan before the debounce time has elapsed");
            h.Catalog.List().ShouldBeEmpty();
            await AdvanceUntilAsync(h, () => h.Catalog.List().Count == 1, "the package is found after the debounce time");
            h.Catalog.List()[0].State.ShouldBe(PackageState.Active);
            h.Catalog.ProcessedCount.ShouldBe(1, "found once");

            // A file another process still holds open is retried, not declared invalid.
            var second = h.UserFile("vendor.second-1.0.0.wpkg");
            var locked = new FileStream(second, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (locked)
            {
                locked.Write(new TestPackage("vendor.second").WithConnector().ToWpkg(h.ReleaseKey));
                locked.Flush();
                await AdvanceUntilAsync(h, () => h.Logs.WithEventId(1809).Any(), "the locked file is deferred");
                h.Catalog.List().Count.ShouldBe(1);
            }

            await AdvanceUntilAsync(h, () => h.Catalog.List().Count == 2, "the file is picked up once it is released");
            h.Catalog.List().ShouldAllBe(p => p.State == PackageState.Active);
            h.Catalog.ProcessedCount.ShouldBe(2, "each package verified once");
        }

        // --- Lost notifications: an overflow, then the periodic rescan, find what no event announced.
        await using (var h = new RuntimeHarness())
        {
            var source = new ManualChangeSource();
            var watcher = h.Watcher(source);
            await watcher.StartAsync(Ct);
            source.Started.ShouldBeTrue();

            wpkg.WriteWpkg(h.UserFile("vendor.acme-1.0.0.wpkg"), h.ReleaseKey);
            source.RaiseOverflow();
            h.Logs.WithEventId(1808).ShouldHaveSingleItem();
            h.Clock.Advance(h.Options.Debounce);
            await Eventually.UntilAsync(() => h.Catalog.List().Count == 1, because: "an overflow triggers a full rescan", ct: Ct);

            new TestPackage("vendor.second").WithConnector().WriteWpkg(h.UserFile("vendor.second-1.0.0.wpkg"), h.ReleaseKey);
            h.Clock.Advance(h.Options.RescanInterval);
            await Eventually.UntilAsync(() => h.Catalog.List().Count == 2, because: "the periodic rescan is the safety net", ct: Ct);

            // Rescans of an unchanged folder verify nothing again.
            var scans = watcher.ScansCompleted;
            h.Clock.Advance(h.Options.RescanInterval);
            await Eventually.UntilAsync(() => watcher.ScansCompleted > scans, ct: Ct);
            h.Catalog.ProcessedCount.ShouldBe(2);
        }
    }

    [Fact]
    public async Task IRT_U_017_Activation_registers_types_with_MOD()
    {
        await using var h = new RuntimeHarness();
        var changes = new List<PackageChangedEventArgs>();
        h.Catalog.Changed += (_, e) => changes.Add(e);

        var package = await h.InstallAsync(new TestPackage().WithConnector());

        package.State.ShouldBe(PackageState.Active);
        var documents = h.Types.Documents("vendor.acme", "1.0.0").ShouldNotBeNull();
        documents.Types.ShouldHaveSingleItem().Id.Value.ShouldBe("vendor.acme.server");
        documents.Translations.Keys.Order().ShouldBe(["ar", "en", "fr"]);
        var change = changes.ShouldHaveSingleItem();
        change.Package.ShouldBe(package);
        change.PreviousState.ShouldBeNull();
        h.Logs.WithEventId(1803).ShouldHaveSingleItem();
        h.Catalog.Get(_acme, _v1).Value.ShouldBe(package);
        h.Catalog.CountByState().ShouldBe(new Dictionary<PackageState, int> { [PackageState.Active] = 1 });

        // A package MOD refuses is never half-registered: invalid, nothing cached, nothing loadable.
        h.Types.Refuse = (_, _) => Error.Conflict("model.document.conflict");
        var refused = await h.InstallAsync(new TestPackage("vendor.refused").WithConnector());
        refused.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(refused).ShouldBe(["model.document.conflict"]);
        h.Types.IsRegistered(refused.Id, refused.Version).ShouldBeFalse();
        Directory.Exists(Path.Combine(h.Options.CachePath, "vendor.refused")).ShouldBeFalse();
        h.Catalog.GetLoadable(refused.Id, refused.Version).IsSuccess.ShouldBeFalse();
        h.Catalog.GetLatestActive(refused.Id).Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotFound);
        h.Logs.WithEventId(1802).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task IRT_U_018_Two_versions_active_simultaneously()
    {
        await using var h = new RuntimeHarness();
        (await h.InstallAsync(new TestPackage("vendor.acme", "1.0.0").WithConnector())).State.ShouldBe(PackageState.Active);
        (await h.InstallAsync(new TestPackage("vendor.acme", "1.1.0").WithConnector())).State.ShouldBe(PackageState.Active);

        h.Catalog.List().Select(p => (p.Version.ToString(), p.State)).ShouldBe([("1.0.0", PackageState.Active), ("1.1.0", PackageState.Active)]);
        h.Catalog.GetLatestActive(_acme).Value.Version.ShouldBe(new SemVersion(1, 1, 0));

        // The same id and version from a second file is refused, whatever it contains; the first one stays.
        new TestPackage("vendor.acme", "1.1.0").WithConnector().WithFile("docs/en.md", "other content").WriteWpkg(h.UserFile("copy.wpkg"), h.ReleaseKey);
        await h.Catalog.ScanAsync(Ct);
        var copy = h.Catalog.List().Single(p => p.FilePath == h.UserFile("copy.wpkg"));
        copy.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(copy).ShouldBe([IntegrationErrors.PackageVersionConflict]);
        h.Catalog.Get(_acme, new SemVersion(1, 1, 0)).Value.State.ShouldBe(PackageState.Active);
        File.Delete(h.UserFile("copy.wpkg"));
        await h.Catalog.ScanAsync(Ct);
        h.Catalog.List().Count.ShouldBe(2);

        // Concurrent add, replace, enable/disable and remove: readers always see whole packages.
        h.Types.Refuse = (_, version) => version.Minor == 7 ? Error.Conflict("model.document.conflict") : null;
        var violations = new ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var package in h.Catalog.List())
                {
                    if (package.State is PackageState.Active or PackageState.Disabled && !h.Types.IsRegistered(package.Id, package.Version))
                    {
                        violations.Enqueue($"{package.Version} is {package.State} but its types are not registered");
                    }

                    if (package.Version.Minor == 7 && (package.State != PackageState.Invalid || h.Types.IsRegistered(package.Id, package.Version)))
                    {
                        violations.Enqueue($"{package.Version} failed to load but is {package.State}");
                    }
                }

                if (!h.Catalog.Get(_acme, _v1).IsSuccess)
                {
                    violations.Enqueue("1.0.0 disappeared while it was replaced");
                }
            }
        })).ToList();

        var writers = Enumerable.Range(2, 8).Select(minor => Task.Run(() => h.InstallAsync(new TestPackage("vendor.acme", $"1.{minor}.0").WithConnector())))
            .Concat(Enumerable.Range(0, 4).Select(i => Task.Run(() => h.InstallAsync(new TestPackage("vendor.acme", "1.0.0").WithConnector().WithFile("docs/en.md", $"revision {i}")))))
            .Concat(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                (await h.Catalog.SetEnabledAsync(_acme, new SemVersion(1, 1, 0), i % 2 == 0, RuntimeHarness.Actor, Ct)).IsSuccess.ShouldBeTrue();
            })))
            .Concat(Enumerable.Range(0, 4).Select(_ => Task.Run(() => h.Catalog.ScanAsync(Ct))))
            .ToList();
        await Task.WhenAll(writers);

        var afterAdds = h.Catalog.List();
        afterAdds.Count(p => p.State is PackageState.Active or PackageState.Disabled).ShouldBe(9, "1.0–1.9 without 1.7");
        var failed = afterAdds.Single(p => p.Version == new SemVersion(1, 7, 0));
        failed.State.ShouldBe(PackageState.Invalid);
        Directory.Exists(Path.Combine(h.Options.CachePath, "vendor.acme", "1.7.0")).ShouldBeFalse("a failed load leaves no cached copy");
        foreach (var package in afterAdds.Where(p => p.State != PackageState.Invalid))
        {
            File.Exists(Path.Combine(h.Options.CachePath, "vendor.acme", package.Version.ToString(), "manifest.json")).ShouldBeTrue();
        }

        // Concurrent removal: files deleted while scans run.
        var removals = Directory.EnumerateFiles(h.Options.PackagesPath, "*.wpkg").Where(f => !f.EndsWith("vendor.acme-1.0.0.wpkg", StringComparison.Ordinal))
            .Select(file => Task.Run(async () =>
            {
                File.Delete(file);
                await h.Catalog.ScanAsync(Ct);
            })).ToList();
        await Task.WhenAll(removals);
        await h.Catalog.ScanAsync(Ct);
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        violations.ShouldBeEmpty();
        h.Catalog.List().Select(p => p.Version.ToString()).ShouldBe(["1.0.0"]);
        Directory.GetDirectories(Path.Combine(h.Options.CachePath, "vendor.acme")).Select(Path.GetFileName).ShouldBe(["1.0.0"]);
    }

    [Fact]
    public async Task IRT_U_019_Disable_stops_its_connections()
    {
        await using var h = new RuntimeHarness();
        await h.InstallAsync(new TestPackage("vendor.acme", "1.0.0").WithConnector());
        await h.InstallAsync(new TestPackage("vendor.acme", "1.1.0").WithConnector());
        var changes = new List<PackageChangedEventArgs>();
        h.Catalog.Changed += (_, e) => changes.Add(e);

        (await h.Catalog.SetEnabledAsync(_acme, _v1, enabled: false, RuntimeHarness.Actor, Ct)).IsSuccess.ShouldBeTrue();

        h.Connections.Stopped.ShouldBe([("vendor.acme", "1.0.0", "packageDisabled")]);
        h.Catalog.Get(_acme, _v1).Value.State.ShouldBe(PackageState.Disabled);
        h.Catalog.Get(_acme, new SemVersion(1, 1, 0)).Value.State.ShouldBe(PackageState.Active);
        h.Types.IsRegistered(_acme, _v1).ShouldBeTrue("types stay registered");
        h.Catalog.GetLoadable(_acme, _v1).Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotActive);
        var change = changes.ShouldHaveSingleItem();
        (change.PreviousState, change.Package.State).ShouldBe((PackageState.Active, PackageState.Disabled));
        var row = (await h.Store.ListAsync(Ct)).Single(r => r.Version == "1.0.0");
        (row.State, row.Enabled).ShouldBe(("disabled", false));

        // Disabling again changes nothing; enabling brings it back; unknown and invalid packages are refused.
        (await h.Catalog.SetEnabledAsync(_acme, _v1, enabled: false, RuntimeHarness.Actor, Ct)).IsSuccess.ShouldBeTrue();
        h.Connections.Stopped.Count.ShouldBe(1);
        (await h.Catalog.SetEnabledAsync(_acme, _v1, enabled: true, RuntimeHarness.Actor, Ct)).IsSuccess.ShouldBeTrue();
        h.Catalog.Get(_acme, _v1).Value.State.ShouldBe(PackageState.Active);
        (await h.Catalog.SetEnabledAsync(_acme, new SemVersion(9, 9, 9), false, RuntimeHarness.Actor, Ct)).Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotFound);
        var invalid = await h.InstallAsync(new TestPackage("vendor.unsigned").WithConnector(), unsigned: true);
        (await h.Catalog.SetEnabledAsync(invalid.Id, invalid.Version, true, RuntimeHarness.Actor, Ct)).Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotActive);
    }

    [Fact]
    public async Task IRT_U_023_Install_via_API_path_writes_file_and_returns_verification()
    {
        await using var h = new RuntimeHarness();
        var package = new TestPackage().WithConnector();

        using var upload = new MemoryStream(package.ToWpkg(h.ReleaseKey));
        var installed = await h.Catalog.InstallAsync(upload, "acme.wpkg", RuntimeHarness.Actor, Ct);

        installed.Value.State.ShouldBe(PackageState.Active);
        installed.Value.Source.ShouldBe(PackageInfo.UserSource);
        installed.Value.FilePath.ShouldBe(h.UserFile("acme.wpkg"));
        File.ReadAllBytes(h.UserFile("acme.wpkg")).ShouldBe(upload.ToArray());
        Directory.GetFiles(h.Options.PackagesPath).ShouldBe([h.UserFile("acme.wpkg")], "no temporary file is left");
        h.Logs.WithEventId(1815).ShouldHaveSingleItem();

        // The watcher's next scan does not verify the uploaded file a second time.
        await h.Catalog.ScanAsync(Ct);
        h.Catalog.ProcessedCount.ShouldBe(1);

        // An invalid package is written and reported with its problems.
        using var unsigned = new MemoryStream(new TestPackage("vendor.unsigned").WithConnector().ToWpkg(null));
        var invalid = await h.Catalog.InstallAsync(unsigned, "unsigned.wpkg", RuntimeHarness.Actor, Ct);
        invalid.Value.State.ShouldBe(PackageState.Invalid);
        RuntimeHarness.ErrorCodes(invalid.Value).ShouldBe([IntegrationErrors.PackageUnsigned]);
        File.Exists(h.UserFile("unsigned.wpkg")).ShouldBeTrue();

        // File names that could leave the folder or hide from the scan are refused before anything is written.
        foreach (var name in new[] { "../evil.wpkg", "sub/evil.wpkg", "..\\evil.wpkg", ".hidden.wpkg", "evil.zip", "evil.wpkg.exe", "a b.wpkg", string.Empty, ".wpkg" })
        {
            using var stream = new MemoryStream(package.ToWpkg(h.ReleaseKey));
            var refused = await h.Catalog.InstallAsync(stream, name, RuntimeHarness.Actor, Ct);
            refused.IsSuccess.ShouldBeFalse(name);
            refused.Error.Code.Value.ShouldBe(IntegrationErrors.PackageFileNameInvalid, name);
        }

        Directory.GetFiles(h.Root, "evil*", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    [Fact]
    public async Task IRT_U_024_Settings_schema_exposed_with_secrets_fields_and_UI_hints()
    {
        await using var h = new RuntimeHarness();
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["host"] = new JsonObject { ["type"] = "string" },
                ["port"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 65535, ["default"] = 443 },
            },
            ["required"] = new JsonArray("host"),
            ["x-wp-ui"] = new JsonObject { ["order"] = new JsonArray("host", "port") },
        };
        var package = await h.InstallAsync(new TestPackage().WithConnector(settingsSchema: schema));
        package.State.ShouldBe(PackageState.Active, string.Join("; ", package.Problems));

        var exposed = h.Catalog.GetSettingsSchema(_acme, _v1, "vendor.acme.server").Value;

        exposed.ServerType.ShouldBe("vendor.acme.server");
        exposed.Settings.GetProperty("properties").GetProperty("port").GetProperty("default").GetInt32().ShouldBe(443);
        exposed.Secrets.ShouldBe([new SecretFieldSchema("password", "password", true)]);
        exposed.UiHints.ShouldNotBeNull().GetProperty("order")[1].GetString().ShouldBe("port");

        h.Catalog.GetSettingsSchema(_acme, _v1, "vendor.acme.other").Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotFound);
        h.Catalog.GetSettingsSchema(_acme, new SemVersion(2, 0, 0), "vendor.acme.server").Error.Code.Value.ShouldBe(IntegrationErrors.PackageNotFound);
    }

    [Fact]
    public async Task IRT_U_025_Removed_when_file_deleted_and_unused()
    {
        await using var h = new RuntimeHarness();
        await h.InstallAsync(new TestPackage("vendor.used").WithConnector());
        await h.InstallAsync(new TestPackage("vendor.unused").WithConnector());
        var used = PackageId.FromTrusted("vendor.used");
        h.Connections.Use("vendor.used", "1.0.0");
        var changes = new List<PackageChangedEventArgs>();
        h.Catalog.Changed += (_, e) => changes.Add(e);

        File.Delete(h.UserFile("vendor.used-1.0.0.wpkg"));
        File.Delete(h.UserFile("vendor.unused-1.0.0.wpkg"));
        await h.Catalog.ScanAsync(Ct);

        // Unused: removed (types stay registered, the cached copy goes).
        h.Catalog.List().Select(p => p.Id.Value).ShouldBe(["vendor.used"]);
        changes.Single(c => c.Package.Id.Value == "vendor.unused").Package.State.ShouldBe(PackageState.Removed);
        (await h.Store.ListAsync(Ct)).Single(r => r.Id == "vendor.unused").State.ShouldBe("removed");
        Directory.Exists(Path.Combine(h.Options.CachePath, "vendor.unused", "1.0.0")).ShouldBeFalse();
        h.Types.IsRegistered(PackageId.FromTrusted("vendor.unused"), _v1).ShouldBeTrue();

        // Used: stays active, with a warning.
        var kept = h.Catalog.Get(used, _v1).Value;
        kept.State.ShouldBe(PackageState.Active);
        var warning = kept.Problems.ShouldHaveSingleItem();
        (warning.Severity, warning.Code.Value).ShouldBe((ProblemSeverity.Warning, IntegrationErrors.PackageFileMissing));
        h.Logs.WithEventId(1807).ShouldHaveSingleItem();
        h.Catalog.GetLoadable(used, _v1).IsSuccess.ShouldBeTrue("its connections can still load it from the cache");
        await h.Catalog.ScanAsync(Ct);
        h.Catalog.Get(used, _v1).Value.Problems.Count.ShouldBe(1, "the warning is not repeated");

        // Once no connection uses it, the next scan removes it.
        h.Connections.Release("vendor.used", "1.0.0");
        await h.Catalog.ScanAsync(Ct);
        h.Catalog.List().ShouldBeEmpty();
        h.Logs.WithEventId(1806).Count().ShouldBe(2);
    }

    /// <summary>Advances the fake clock by the debounce time until <paramref name="condition"/> holds (bounded by a real timeout).</summary>
    private static Task AdvanceUntilAsync(RuntimeHarness h, Func<bool> condition, string because) =>
        Eventually.UntilAsync(
            () =>
            {
                if (condition())
                {
                    return true;
                }

                h.Clock.Advance(h.Options.Debounce);
                return false;
            },
            because: because,
            ct: Ct);
}
