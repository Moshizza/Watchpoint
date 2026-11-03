using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Ids;

/// <summary>Marker of the string-based ids (allowed as error and message arguments).</summary>
internal interface IStringId
{
    string Value { get; }
}

/// <summary>A string-based id with validation.</summary>
/// <typeparam name="TSelf">The id type itself.</typeparam>
internal interface IStringId<TSelf> : IStringId
    where TSelf : struct, IStringId<TSelf>
{
    static abstract Result<TSelf> Parse(string? text);
}
