using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Catalogue;

/// <summary>Generates <c>commands.&lt;capability&gt;.&lt;command&gt;</c> permissions from the MOD catalogue and regenerates them when it changes (spec RGT §4.1).</summary>
internal sealed class CommandPermissionGenerator : IDisposable
{
    private readonly ITypeCatalog _types;
    private readonly IPermissionCatalogue _catalogue;

    public CommandPermissionGenerator(ITypeCatalog types, IPermissionCatalogue catalogue)
    {
        _types = types;
        _catalogue = catalogue;
        _types.Changed += OnChanged;
        Regenerate();
    }

    public void Regenerate()
    {
        foreach (var type in _types.List(new TypeQuery()))
        {
            foreach (var path in type.Commands.Keys)
            {
                _catalogue.Register(CatalogueV1.Command(PermissionCode.FromTrusted("commands." + path.Value)));
            }
        }
    }

    public void Dispose() => _types.Changed -= OnChanged;

    private void OnChanged(object? sender, CatalogChangedEventArgs e) => Regenerate();
}
