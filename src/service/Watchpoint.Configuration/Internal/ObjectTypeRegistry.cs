using Watchpoint.Configuration.Contracts;

namespace Watchpoint.Configuration.Internal;

/// <summary>The registered object types (spec CFG §4): lookup by name, registration order kept (the order publishing applies them in).</summary>
internal sealed class ObjectTypeRegistry : IConfigObjectTypeRegistry
{
    private readonly Lock _gate = new();
    private readonly List<IConfigurableObjectType> _types = [];
    private readonly Dictionary<string, IConfigurableObjectType> _byName = new(StringComparer.Ordinal);

    public IReadOnlyList<IConfigurableObjectType> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _types];
            }
        }
    }

    public void Register(IConfigurableObjectType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (_gate)
        {
            if (!_byName.TryAdd(type.Type, type))
            {
                throw new InvalidOperationException($"Object type '{type.Type}' is already registered.");
            }

            _types.Add(type);
        }
    }

    public IConfigurableObjectType? Find(string name)
    {
        lock (_gate)
        {
            return _byName.GetValueOrDefault(name);
        }
    }
}
