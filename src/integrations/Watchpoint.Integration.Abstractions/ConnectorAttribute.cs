namespace Watchpoint.Integration.Abstractions;

/// <summary>Marks a connector class and names the server type it implements (INT §4).</summary>
/// <param name="serverType">The server type id, as declared in the package manifest.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConnectorAttribute(string serverType) : Attribute
{
    /// <summary>Gets the server type id implemented by the connector.</summary>
    public string ServerType { get; } = string.IsNullOrWhiteSpace(serverType)
        ? throw new ArgumentException("A server type is required.", nameof(serverType))
        : serverType;
}
