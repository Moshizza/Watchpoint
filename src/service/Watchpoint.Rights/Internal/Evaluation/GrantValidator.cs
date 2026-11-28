using Watchpoint.Kernel.Results;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Catalogue;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>Validates the grants of one user (spec RGT §5): permission, constraints, scope kind, duplicates.</summary>
internal sealed class GrantValidator(PermissionCatalogueAccess catalogue)
{
    public IReadOnlyList<Error> Validate(IReadOnlyList<RightGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        var errors = new List<Error>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            var code = grant.Permission.Value;
            var definitions = catalogue.Expand(grant.Permission);
            if (definitions.Count == 0)
            {
                errors.Add(RightsErrors.PermissionUnknown(code));
                continue;
            }

            foreach (var constraint in UsedConstraints(grant.Constraints))
            {
                if (definitions.Any(d => !d.AllowedConstraints.Contains(constraint)))
                {
                    errors.Add(RightsErrors.ConstraintNotAllowed(code, constraint));
                }
            }

            if (!ScopeIsConsistent(grant.Scope))
            {
                errors.Add(RightsErrors.InvalidScope(code, "everything_with_lists"));
            }
            else if (definitions.Any(d => d.ScopeKind == PermissionScopeKind.None) && !grant.Scope.Everything)
            {
                errors.Add(RightsErrors.InvalidScope(code, "site_wide_only"));
            }
            else if (definitions.Any(d => d.ScopeKind == PermissionScopeKind.System) && !grant.Scope.Everything)
            {
                errors.Add(RightsErrors.InvalidScope(code, "system_only"));
            }
            else if (!grant.Scope.Everything && IsEmpty(grant.Scope))
            {
                errors.Add(RightsErrors.InvalidScope(code, "empty"));
            }

            if (!seen.Add(GrantIdentity.Canonical(grant)))
            {
                errors.Add(RightsErrors.Duplicate(code));
            }
        }

        return errors;
    }

    internal static IEnumerable<ConstraintKind> UsedConstraints(GrantConstraints c)
    {
        if (c.RequireReason)
        {
            yield return ConstraintKind.RequireReason;
        }

        if (c.RequireFourEyes)
        {
            yield return ConstraintKind.RequireFourEyes;
        }

        if (c.RequireStepUp)
        {
            yield return ConstraintKind.RequireStepUp;
        }

        if (c.MaxDuration is not null)
        {
            yield return ConstraintKind.MaxDuration;
        }

        if (c.Priorities is not null)
        {
            yield return ConstraintKind.PriorityRange;
        }

        if (c.ClientKinds is not null)
        {
            yield return ConstraintKind.ClientKinds;
        }

        if (c.MaxLookback is not null)
        {
            yield return ConstraintKind.MaxLookback;
        }
    }

    private static bool ScopeIsConsistent(GrantScope s) =>
        !s.Everything || (s.LocationsSubtree is null && s.Servers is null && s.Categories is null && s.Types is null && s.TagsAny is null
            && s.TagsExclude is null && s.Devices is null && s.Sites is null);

    private static bool IsEmpty(GrantScope s) =>
        s.LocationsSubtree is null && s.Servers is null && s.Categories is null && s.Types is null && s.TagsAny is null
        && s.TagsExclude is null && s.Devices is null && s.Sites is null;
}
