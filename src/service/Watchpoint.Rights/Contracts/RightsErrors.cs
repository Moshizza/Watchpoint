using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Rights.Contracts;

/// <summary>Errors of the rights module (spec RGT §13).</summary>
public static class RightsErrors
{
    /// <summary>Unknown permission.</summary>
    public const string PermissionUnknownCode = "rights.permission.unknown";

    /// <summary>Scope not valid for the permission.</summary>
    public const string InvalidScopeCode = "rights.grant.invalid_scope";

    /// <summary>Constraint not allowed for the permission.</summary>
    public const string ConstraintNotAllowedCode = "rights.grant.constraint_not_allowed";

    /// <summary>Two grants with the same identity.</summary>
    public const string DuplicateCode = "rights.grant.duplicate";

    /// <summary>Outside the grant schedule.</summary>
    public const string OutsideScheduleCode = "rights.outside_schedule";

    /// <summary>Client kind not allowed.</summary>
    public const string ClientKindCode = "rights.client_kind";

    /// <summary>User group not found.</summary>
    public const string GroupNotFoundCode = "rights.group.not_found";

    /// <summary>Unknown permission.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The error.</returns>
    public static Error PermissionUnknown(string code) => Error.Create(ErrorKind.Validation, PermissionUnknownCode, ("permission", code));

    /// <summary>Invalid scope.</summary>
    /// <param name="permission">The permission.</param>
    /// <param name="reason">What is wrong.</param>
    /// <returns>The error.</returns>
    public static Error InvalidScope(string permission, string reason) => Error.Create(ErrorKind.Validation, InvalidScopeCode, ("permission", permission), ("reason", reason));

    /// <summary>Constraint not allowed.</summary>
    /// <param name="permission">The permission.</param>
    /// <param name="constraint">The constraint.</param>
    /// <returns>The error.</returns>
    public static Error ConstraintNotAllowed(string permission, ConstraintKind constraint) => Error.Create(ErrorKind.Validation, ConstraintNotAllowedCode, ("permission", permission), ("constraint", constraint.ToString()));

    /// <summary>Duplicate grant.</summary>
    /// <param name="permission">The permission.</param>
    /// <returns>The error.</returns>
    public static Error Duplicate(string permission) => Error.Create(ErrorKind.Conflict, DuplicateCode, ("permission", permission));

    /// <summary>Group not found.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The error.</returns>
    public static Error GroupNotFound(UserGroupId id) => Error.Create(ErrorKind.NotFound, GroupNotFoundCode, ("id", id.Value.ToString()));
}
