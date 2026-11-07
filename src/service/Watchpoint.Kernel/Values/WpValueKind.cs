using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Kernel.Values;

/// <summary>The kind of a <see cref="WpValue"/>.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Member names fixed by spec K §10.")]
public enum WpValueKind
{
    /// <summary>No value (<c>{"k":"null"}</c>); <see langword="default"/> of <see cref="WpValue"/>.</summary>
    Null,

    /// <summary>Boolean (<c>bool</c>).</summary>
    Bool,

    /// <summary>64-bit integer (<c>int</c>).</summary>
    Int,

    /// <summary>Decimal number, written as a string to keep precision (<c>dec</c>).</summary>
    Decimal,

    /// <summary>Code of an enumeration member, pattern <c>^[a-z][a-zA-Z0-9]*$</c> (<c>enum</c>).</summary>
    Enum,

    /// <summary>Text (<c>str</c>).</summary>
    String,

    /// <summary>UTC instant with millisecond precision (<c>ts</c>).</summary>
    Timestamp,

    /// <summary>Duration, ISO 8601 in JSON (<c>dur</c>).</summary>
    Duration,

    /// <summary>Geographic position (<c>geo</c>).</summary>
    Geo,

    /// <summary>List of values (<c>list</c>).</summary>
    List,

    /// <summary>JSON object (<c>obj</c>).</summary>
    Object,
}
