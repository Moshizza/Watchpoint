namespace Watchpoint.State.Contracts;

/// <summary>What an <see cref="AggregateScope"/> counts over (spec STA §5).</summary>
public enum AggregateScopeKind
{
    /// <summary>A location and its subtree.</summary>
    Location,

    /// <summary>A server and its child servers.</summary>
    Server,

    /// <summary>A tag.</summary>
    Tag,

    /// <summary>The whole site.</summary>
    Site,
}

/// <summary>Identifies one scope counted by <see cref="IAggregateReader"/> (spec STA §5).</summary>
/// <param name="Kind">What the scope counts over.</param>
/// <param name="Id">The location, server or tag id, or the site id for <see cref="AggregateScopeKind.Site"/>.</param>
public readonly record struct AggregateScope(AggregateScopeKind Kind, Guid Id);
