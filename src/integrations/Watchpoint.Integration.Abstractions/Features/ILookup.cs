using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Looks up details in the vendor system, e.g. a cardholder by reference, for operator enrichment (INT §6).</summary>
public interface ILookup
{
    /// <summary>Looks up an item.</summary>
    /// <param name="request">What to look up.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    ValueTask<LookupResult> LookupAsync(LookupRequest request, CancellationToken ct);
}

/// <summary>A lookup request.</summary>
/// <param name="Kind">What is looked up, as declared in the manifest, e.g. <c>cardholder</c>.</param>
/// <param name="Reference">The vendor reference, e.g. from an event payload.</param>
/// <param name="Device">The device the reference came from, if any.</param>
public sealed record LookupRequest(string Kind, string Reference, DeviceKey? Device);

/// <summary>A lookup result.</summary>
/// <param name="Found">Whether the item exists.</param>
/// <param name="Fields">The item's fields (name → value); empty when not found.</param>
public sealed record LookupResult(bool Found, IReadOnlyDictionary<string, WpValue> Fields)
{
    /// <summary>The item does not exist.</summary>
    public static LookupResult NotFound { get; } = new(false, new Dictionary<string, WpValue>());
}
