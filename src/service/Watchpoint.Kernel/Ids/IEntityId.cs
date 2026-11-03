using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Kernel.Ids;

/// <summary>
/// A strongly typed identifier wrapping a UUID (version 7 when created by Watchpoint).
/// </summary>
/// <typeparam name="TSelf">The id type itself.</typeparam>
public interface IEntityId<TSelf> : IEquatable<TSelf>, IComparable<TSelf>, ISpanFormattable
    where TSelf : struct, IEntityId<TSelf>
{
    /// <summary>Gets the wrapped UUID value. Never <see cref="Guid.Empty"/> for a valid id.</summary>
    Guid Value { get; }

    /// <summary>Gets the short prefix used only in logs and debugging, e.g. <c>dev</c>.</summary>
    static abstract string Prefix { get; }

    /// <summary>Wraps an existing UUID.</summary>
    /// <param name="value">The UUID; must not be <see cref="Guid.Empty"/>.</param>
    /// <returns>The id.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is <see cref="Guid.Empty"/>.</exception>
    static abstract TSelf From(Guid value);

    /// <summary>Creates a new id from a version 7 UUID (sortable by creation time).</summary>
    /// <returns>The new id.</returns>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec K §4.1.")]
    static abstract TSelf New();

    /// <summary>
    /// Parses a UUID in the lowercase or uppercase <c>D</c> format (36 characters with dashes).
    /// Braces, missing dashes and <see cref="Guid.Empty"/> are rejected.
    /// </summary>
    /// <param name="s">The text to parse.</param>
    /// <param name="id">The parsed id when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="s"/> is a valid id.</returns>
    static abstract bool TryParse([NotNullWhen(true)] string? s, out TSelf id);
}
