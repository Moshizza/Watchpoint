using Watchpoint.History.Internal;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>Tag keys from a dictionary.</summary>
internal sealed class FakeTagKeys : IHistoryTagKeys
{
    public Dictionary<TagId, string> Tags { get; } = [];

    public IReadOnlyList<string> KeysOf(IReadOnlyCollection<TagId> tags) => [.. tags.Where(Tags.ContainsKey).Select(t => Tags[t])];
}
