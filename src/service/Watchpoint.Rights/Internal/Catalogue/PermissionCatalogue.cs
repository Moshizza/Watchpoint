using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Catalogue;

/// <summary>The in-memory permission catalogue seeded with catalogue v1 (spec RGT §4.1).</summary>
internal sealed class PermissionCatalogue : IPermissionCatalogue
{
    private readonly ConcurrentDictionary<string, PermissionDefinition> _byCode = new(StringComparer.Ordinal);

    public PermissionCatalogue()
    {
        foreach (var definition in CatalogueV1.Definitions())
        {
            Register(definition);
        }
    }

    public IReadOnlyList<PermissionDefinition> All => [.. _byCode.Values.OrderBy(d => d.Code.Value, StringComparer.Ordinal)];

    public Result<PermissionDefinition> Get(PermissionCode code) =>
        _byCode.TryGetValue(code.Value, out var definition) ? definition : RightsErrors.PermissionUnknown(code.Value);

    public bool Matches(PermissionCode grantPattern, PermissionCode requested)
    {
        var pattern = grantPattern.Value;
        var code = requested.Value;
        if (pattern == "*")
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            return code.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.Ordinal);
        }

        return string.Equals(pattern, code, StringComparison.Ordinal);
    }

    public void Register(PermissionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _byCode[definition.Code.Value] = definition;
    }
}
