using Watchpoint.Kernel.Ids;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>Adapts a property map to the view MOD's state summary evaluator reads (spec STA §6.1).</summary>
internal sealed class PropertyMapView(IReadOnlyDictionary<PropertyPath, PropertyState> properties) : IDeviceStateView
{
    public bool TryGet(PropertyPath path, out PropertyValueView value)
    {
        if (properties.TryGetValue(path, out var state))
        {
            value = new PropertyValueView(state.Value, state.Quality);
            return true;
        }

        value = default;
        return false;
    }
}
