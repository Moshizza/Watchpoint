namespace Watchpoint.Testing.Json;

/// <summary>Options for <see cref="JsonAssert.Equivalent(string, string, JsonAssertOptions?)"/>.</summary>
public sealed record JsonAssertOptions
{
    /// <summary>Gets the JSON pointers (RFC 6901, e.g. <c>/traceId</c>) whose values are not compared.</summary>
    public IReadOnlyCollection<string> IgnoredPaths { get; init; } = [];

    /// <summary>Creates options ignoring the given JSON pointers.</summary>
    /// <param name="paths">The JSON pointers.</param>
    /// <returns>The options.</returns>
    public static JsonAssertOptions Ignoring(params string[] paths) => new() { IgnoredPaths = paths };
}
