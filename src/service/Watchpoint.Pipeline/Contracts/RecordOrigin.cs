namespace Watchpoint.Pipeline.Contracts;

/// <summary>Who produced a record (spec PIP §5.2, <c>origin.type</c>).</summary>
public enum RecordOriginType
{
    /// <summary>An integration connection.</summary>
    Connection = 0,

    /// <summary>A user.</summary>
    User = 1,

    /// <summary>The node itself.</summary>
    System = 2,

    /// <summary>An automation.</summary>
    Automation = 3,

    /// <summary>A script.</summary>
    Script = 4,

    /// <summary>Another site through federation.</summary>
    Federation = 5,
}

/// <summary>The origin of a record (spec PIP §5.2).</summary>
/// <param name="Type">The kind of origin.</param>
/// <param name="Id">The id of the connection, user, automation…; <see langword="null"/> for the system.</param>
public sealed record RecordOrigin(RecordOriginType Type, Guid? Id)
{
    /// <summary>The node itself.</summary>
    public static RecordOrigin System { get; } = new(RecordOriginType.System, null);
}
