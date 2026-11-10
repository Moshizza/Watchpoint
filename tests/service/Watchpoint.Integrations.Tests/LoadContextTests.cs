using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Shouldly;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Tests.Support;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Integrations.Tests;

/// <summary>Load contexts (spec IRT §5, ADR-003): shared assemblies, private dependencies, native libraries, unloading.</summary>
[Trait("layer", "U")]
[Trait("spec", "IRT")]
public sealed class LoadContextTests
{
    private const string DependencySource = """
        namespace Vendor.Dep;
        public static class Info { public static string Version => typeof(Info).Assembly.GetName().Version!.ToString(2); }
        """;

    [Fact]
    public async Task IRT_U_020_Shared_assemblies_resolved_from_host()
    {
        await using var h = new RuntimeHarness();
        var package = new TestPackage()
            .WithConnector(members: "public Type ValueType() => typeof(Watchpoint.Kernel.Values.WpValue); public Type SdkType() => typeof(Watchpoint.Integration.Sdk.Resilience.ResiliencePresets);")

            // The package ships its own copies of contract assemblies: they are reported and never loaded.
            .WithFile("lib/Watchpoint.Kernel.dll", File.ReadAllBytes(typeof(WpValue).Assembly.Location))
            .WithFile("lib/Watchpoint.Integration.Abstractions.dll", File.ReadAllBytes(typeof(IConnector).Assembly.Location));

        var info = await h.InstallAsync(package);

        info.State.ShouldBe(PackageState.Active);
        info.Problems.Where(p => p.Code.Value == IntegrationErrors.PackageSharedAssemblyShadowed).Select(p => p.Severity).ShouldBe([ProblemSeverity.Warning, ProblemSeverity.Warning]);
        h.Logs.WithEventId(1814).Count().ShouldBe(2);

        var context = UseConnector(h, package, connector =>
        {
            // Type identity across the boundary: the connector is the host's IConnector and sees the host's WpValue.
            connector.ShouldBeAssignableTo<IConnector>();
            Invoke<Type>(connector, "ValueType").ShouldBeSameAs(typeof(WpValue));
            Invoke<Type>(connector, "ValueType").Assembly.ShouldBeSameAs(typeof(WpValue).Assembly);
            Invoke<Type>(connector, "SdkType").Assembly.ShouldBeSameAs(typeof(Watchpoint.Integration.Sdk.Resilience.ResiliencePresets).Assembly);
            var own = AssemblyLoadContext.GetLoadContext(connector.GetType().Assembly)!;
            own.IsCollectible.ShouldBeTrue();
            own.Name.ShouldBe("vendor.acme@1.0.0");
            own.Assemblies.Select(a => a.GetName().Name).ShouldBe(["Vendor.Acme"], "contract assemblies are not loaded into the package context");
        });

        // No leak: once the last lease is returned the context is unloaded and collected.
        IsCollected(context).ShouldBeTrue("the load context must be collectible after unload");
        h.Pool.IsLoaded(info.Id, info.Version).ShouldBeFalse();
        h.Pool.NotCollected(TimeSpan.Zero).ShouldBeEmpty();
    }

    [Fact]
    public async Task IRT_U_021_Private_dependency_isolation()
    {
        await using var h = new RuntimeHarness();
        var packageA = new TestPackage("vendor.alpha").WithConnector("public string DepVersion() => Vendor.Dep.Info.Version;",
            privateDependencies: ("Vendor.Dep", TestAssemblies.Compile("Vendor.Dep", DependencySource, "1.0.0.0")));
        var packageB = new TestPackage("vendor.beta").WithConnector("public string DepVersion() => Vendor.Dep.Info.Version;",
            privateDependencies: ("Vendor.Dep", TestAssemblies.Compile("Vendor.Dep", DependencySource, "2.0.0.0")));
        (await h.InstallAsync(packageA)).State.ShouldBe(PackageState.Active);
        (await h.InstallAsync(packageB)).State.ShouldBe(PackageState.Active);

        var contexts = UseBoth(h, packageA, packageB);

        AssemblyLoadContext.Default.Assemblies.ShouldNotContain(a => a.GetName().Name == "Vendor.Dep", "private dependencies never reach the host");
        IsCollected(contexts.A).ShouldBeTrue();
        IsCollected(contexts.B).ShouldBeTrue();
    }

    [Fact]
    public async Task IRT_U_022_Native_library_resolution_by_RID()
    {
        await using var h = new RuntimeHarness();
        var rid = SharedAssemblies.CurrentRuntimeIdentifiers[^1];
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        // Any native library of the runtime serves as the package's "foo" (no compiler needed at test time).
        var (source, fileName) = OperatingSystem.IsWindows()
            ? (File.Exists(Path.Combine(runtimeDir, "clretwrc.dll")) ? "clretwrc.dll" : "System.IO.Compression.Native.dll", "foo.dll")
            : OperatingSystem.IsMacOS() ? ("libSystem.Native.dylib", "libfoo.dylib")
            : ("libSystem.Native.so", "libfoo.so");
        var package = new TestPackage().WithConnector()
            .WithFile($"native/{rid}/{fileName}", File.ReadAllBytes(Path.Combine(runtimeDir, source)))
            .WithFile($"native/other-rid/{fileName}", [0]);
        (await h.InstallAsync(package)).State.ShouldBe(PackageState.Active);

        var context = UseConnector(h, package, connector =>
        {
            var own = (PackageLoadContext)AssemblyLoadContext.GetLoadContext(connector.GetType().Assembly)!;
            var expected = Path.Combine(h.Options.CachePath, "vendor.acme", "1.0.0", "native", rid, fileName);
            own.ResolveNativeLibraryPath("foo").ShouldBe(expected);
            own.ResolveNativeLibraryPath("bar").ShouldBeNull();
            own.ResolveNativeLibraryPath("../foo").ShouldBeNull();

            // The way a P/Invoke of the connector's assembly resolves "foo": through its load context.
            var handle = NativeLibrary.Load("foo", connector.GetType().Assembly, null);
            handle.ShouldNotBe(IntPtr.Zero);
            NativeLibrary.Free(handle);
        });

        IsCollected(context).ShouldBeTrue();
    }

    /// <summary>Forces collections until <paramref name="reference"/> is dead (bounded, no waiting).</summary>
    internal static bool IsCollected(WeakReference reference)
    {
        for (var i = 0; i < 20 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !reference.IsAlive;
    }

    // The helpers below keep every reference into a package inside a frame that has returned before collection is checked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UseConnector(RuntimeHarness h, TestPackage package, Action<object> use)
    {
        var (lease, connector) = Create(h, package);
        use(connector);
        var context = new WeakReference(lease.Context);
        lease.Dispose();
        return context;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference A, WeakReference B) UseBoth(RuntimeHarness h, TestPackage packageA, TestPackage packageB)
    {
        var (leaseA, connectorA) = Create(h, packageA);
        var (leaseB, connectorB) = Create(h, packageB);
        var (secondLeaseA, secondConnectorA) = Create(h, packageA);

        // Each package gets its own copy of Vendor.Dep, side by side.
        Invoke<string>(connectorA, "DepVersion").ShouldBe("1.0");
        Invoke<string>(connectorB, "DepVersion").ShouldBe("2.0");
        leaseA.Context.ShouldNotBeSameAs(leaseB.Context);

        // Connections of one package version share one context.
        secondLeaseA.Context.ShouldBeSameAs(leaseA.Context);
        secondConnectorA.GetType().ShouldBeSameAs(connectorA.GetType());
        h.Pool.IsLoaded(Kernel.Ids.PackageId.FromTrusted("vendor.alpha"), new Model.Documents.SemVersion(1, 0, 0)).ShouldBeTrue();

        var result = (new WeakReference(leaseA.Context), new WeakReference(leaseB.Context));
        leaseA.Dispose();
        h.Pool.IsLoaded(Kernel.Ids.PackageId.FromTrusted("vendor.alpha"), new Model.Documents.SemVersion(1, 0, 0)).ShouldBeTrue("a lease is still out");
        secondLeaseA.Dispose();
        leaseB.Dispose();
        return result;
    }

    private static (PackageLease Lease, object Connector) Create(RuntimeHarness h, TestPackage package)
    {
        var id = Kernel.Ids.PackageId.FromTrusted(package.Id);
        var version = Model.Documents.SemVersion.Parse(package.Version).Value;
        var (packageDir, manifest) = h.Catalog.GetLoadable(id, version).Value;
        var connectorClass = manifest.Connectors[0].Class;
        var lease = h.Pool.Acquire(id, version, packageDir, ConnectorCodeChecker.SplitClass(connectorClass).Assembly);
        return (lease, lease.CreateConnector(connectorClass));
    }

    private static T Invoke<T>(object target, string method) =>
        (T)target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, null)!;
}
