using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.State.Tests.Fakes;

/// <summary>A minimal <see cref="ITypeCatalog"/> holding the type definitions a test registers.</summary>
internal sealed class FakeTypeCatalog : ITypeCatalog
{
    private readonly Dictionary<TypeRef, DeviceTypeDefinition> _types = [];

    public long Revision { get; private set; } = 1;

#pragma warning disable CS0067 // Never raised by this fake: no test reloads the catalogue mid-run.
    public event EventHandler<CatalogChangedEventArgs>? Changed;
#pragma warning restore CS0067

    public void Add(DeviceTypeDefinition type)
    {
        _types[type.Ref] = type;
        Revision++;
    }

    public Result<DeviceTypeDefinition> Get(TypeRef type) =>
        _types.TryGetValue(type, out var definition) ? definition : Error.NotFound("model.type.not_found", ("type", type.ToString()));

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) =>
        _types.Values.Where(t => t.Ref.Id == id && t.Ref.Version.Major == major).OrderByDescending(t => t.Ref.Version).FirstOrDefault()
            is { } found
            ? found
            : Error.NotFound("model.type.not_found", ("type", id.Value));

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => [.. _types.Values];

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException("Not used by state module tests.");

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) =>
        throw new NotSupportedException("Not used by state module tests.");

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) =>
        throw new NotSupportedException("Not used by state module tests.");
}
