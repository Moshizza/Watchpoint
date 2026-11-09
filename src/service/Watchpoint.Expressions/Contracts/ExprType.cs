using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Expressions.Contracts;

/// <summary>Represents the data type of an expression or variable in the expression language (spec EXP §4.3).</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Matches spec EXP §4.3 type names.")]
public enum ExprTypeKind
{
    /// <summary>Unknown or unresolved type (e.g. from an erroneous expression).</summary>
    Unknown,

    /// <summary>Null literal or null-valued expression.</summary>
    Null,

    /// <summary>Boolean type (true or false).</summary>
    Bool,

    /// <summary>64-bit signed integer.</summary>
    Int,

    /// <summary>28-decimal digits high-precision decimal number.</summary>
    Decimal,

    /// <summary>Unicode text or enum value.</summary>
    String,

    /// <summary>UTC instant timestamp.</summary>
    Timestamp,

    /// <summary>Time span duration.</summary>
    Duration,

    /// <summary>Homogeneous or heterogeneous list of values.</summary>
    List,

    /// <summary>Structured object (such as device or event) accessed via members.</summary>
    Object,
}

/// <summary>Describes an expression type, including its element type if a list, and allowed enum values if applicable.</summary>
/// <param name="Kind">The base type kind.</param>
/// <param name="ItemType">Item type for list types, or <see langword="null"/>.</param>
/// <param name="EnumValues">Allowed enum values if this type represents an enum property, or <see langword="null"/>.</param>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Matches spec EXP §4.3 type names.")]
public sealed record ExprType(ExprTypeKind Kind, ExprType? ItemType = null, IReadOnlyList<string>? EnumValues = null)
{
    /// <summary>Gets the unknown type instance.</summary>
    public static readonly ExprType Unknown = new(ExprTypeKind.Unknown);

    /// <summary>Gets the null type instance.</summary>
    public static readonly ExprType Null = new(ExprTypeKind.Null);

    /// <summary>Gets the boolean type instance.</summary>
    public static readonly ExprType Bool = new(ExprTypeKind.Bool);

    /// <summary>Gets the integer type instance.</summary>
    public static readonly ExprType Int = new(ExprTypeKind.Int);

    /// <summary>Gets the decimal type instance.</summary>
    public static readonly ExprType Decimal = new(ExprTypeKind.Decimal);

    /// <summary>Gets the string type instance.</summary>
    public static readonly ExprType String = new(ExprTypeKind.String);

    /// <summary>Gets the timestamp type instance.</summary>
    public static readonly ExprType Timestamp = new(ExprTypeKind.Timestamp);

    /// <summary>Gets the duration type instance.</summary>
    public static readonly ExprType Duration = new(ExprTypeKind.Duration);

    /// <summary>Gets the object type instance.</summary>
    public static readonly ExprType Object = new(ExprTypeKind.Object);

    /// <summary>Creates a list type with the given item type.</summary>
    /// <param name="itemType">The type of items in the list.</param>
    /// <returns>A new <see cref="ExprType"/> representing the list.</returns>
    public static ExprType ListOf(ExprType itemType) => new(ExprTypeKind.List, itemType);

    /// <summary>Creates an enum string type with the given allowed enum codes.</summary>
    /// <param name="allowed">The allowed enum codes.</param>
    /// <returns>A new <see cref="ExprType"/> representing the enum string.</returns>
    public static ExprType EnumString(IReadOnlyList<string> allowed) => new(ExprTypeKind.String, null, allowed);

    /// <summary>Returns whether this type represents an enum string.</summary>
    public bool IsEnum => Kind == ExprTypeKind.String && EnumValues is { Count: > 0 };

    /// <summary>Checks whether this type can be mixed with another numeric type in arithmetic or comparison.</summary>
    /// <param name="other">The other type.</param>
    /// <returns><see langword="true"/> if both are numeric.</returns>
    public bool IsNumericWith(ExprType other) =>
        (Kind == ExprTypeKind.Int || Kind == ExprTypeKind.Decimal) &&
        (other.Kind == ExprTypeKind.Int || other.Kind == ExprTypeKind.Decimal);
}
