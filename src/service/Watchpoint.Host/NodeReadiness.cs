namespace Watchpoint.Host;

/// <summary>
/// Service managing node readiness state.
/// </summary>
public interface INodeReadiness
{
    /// <summary>Gets a value indicating whether the node startup has reached Edge phase and is ready.</summary>
    bool IsReady { get; }

    /// <summary>Marks the node as ready.</summary>
    void SetReady();

    /// <summary>Marks the node as shutting down / draining.</summary>
    void SetDraining();
}

/// <summary>
/// Default implementation of <see cref="INodeReadiness"/>.
/// </summary>
public sealed class NodeReadiness : INodeReadiness
{
    private volatile bool _isReady;
    private volatile bool _isDraining;

    /// <inheritdoc />
    public bool IsReady => _isReady && !_isDraining;

    /// <inheritdoc />
    public void SetReady() => _isReady = true;

    /// <inheritdoc />
    public void SetDraining() => _isDraining = true;
}
