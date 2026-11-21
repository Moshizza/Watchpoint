using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>A catalogue of the types a test registers; only <see cref="Get"/> works.</summary>
internal sealed class FakeTypeCatalog : ITypeCatalog
{
    public Dictionary<TypeRef, DeviceTypeDefinition> Types { get; } = [];

    public long Revision => 1;

    public event EventHandler<CatalogChangedEventArgs>? Changed
    {
        add { }
        remove { }
    }

    public Result<DeviceTypeDefinition> Get(TypeRef type) =>
        Types.TryGetValue(type, out var found) ? found : Error.Create(ErrorKind.NotFound, "model.type.not_found");

    public Result<DeviceTypeDefinition> GetLatest(TypeId id, int major) => throw new NotSupportedException();

    public IReadOnlyList<DeviceTypeDefinition> List(TypeQuery query) => throw new NotSupportedException();

    public Result<CapabilityDefinition> GetCapability(CapabilityId id, SemVersion version) => throw new NotSupportedException();

    public Task<Result<CatalogChange>> RegisterPackageAsync(PackageId package, SemVersion packageVersion, PackageTypeDocuments documents, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<Result> UnregisterPackageAsync(PackageId package, SemVersion packageVersion, CancellationToken ct) => throw new NotSupportedException();
}
