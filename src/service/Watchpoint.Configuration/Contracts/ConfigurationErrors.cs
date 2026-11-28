using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Contracts;

/// <summary>The problem and error codes of the configuration module (spec CFG §12). Each has a translation <c>errors.&lt;code&gt;</c> in en, fr and ar.</summary>
public static class ConfigurationErrors
{
    /// <summary><c>config.draft.not_found</c>.</summary>
    public const string DraftNotFound = "config.draft.not_found";

    /// <summary><c>config.draft.not_open</c>.</summary>
    public const string DraftNotOpen = "config.draft.not_open";

    /// <summary><c>config.draft.too_large</c>.</summary>
    public const string DraftTooLarge = "config.draft.too_large";

    /// <summary><c>config.entry.invalid</c>: the entry is malformed (unknown type, content missing or not allowed for the operation).</summary>
    public const string EntryInvalid = "config.entry.invalid";

    /// <summary><c>config.entry.outdated</c> (warning): the live object changed since the entry was started.</summary>
    public const string EntryOutdated = "config.entry.outdated";

    /// <summary><c>config.reference.missing</c>.</summary>
    public const string ReferenceMissing = "config.reference.missing";

    /// <summary><c>config.reference.in_use</c>.</summary>
    public const string ReferenceInUse = "config.reference.in_use";

    /// <summary><c>config.publish.invalid</c>.</summary>
    public const string PublishInvalid = "config.publish.invalid";

    /// <summary><c>config.publish.conflict</c>.</summary>
    public const string PublishConflict = "config.publish.conflict";

    /// <summary><c>config.publish.in_progress</c>.</summary>
    public const string PublishInProgress = "config.publish.in_progress";

    /// <summary><c>config.approval.required</c>.</summary>
    public const string ApprovalRequired = "config.approval.required";

    /// <summary><c>config.approval.self_forbidden</c>.</summary>
    public const string ApprovalSelfForbidden = "config.approval.self_forbidden";

    /// <summary><c>config.lock.held_by</c>.</summary>
    public const string LockHeldBy = "config.lock.held_by";

    /// <summary><c>config.lock.not_supported</c>.</summary>
    public const string LockNotSupported = "config.lock.not_supported";

    /// <summary><c>config.import.invalid_bundle</c>.</summary>
    public const string ImportInvalidBundle = "config.import.invalid_bundle";

    /// <summary><c>config.import.unmapped_reference</c>.</summary>
    public const string ImportUnmappedReference = "config.import.unmapped_reference";

    /// <summary><c>config.version.not_found</c>.</summary>
    public const string VersionNotFound = "config.version.not_found";

    /// <summary>The draft has an entry of a type whose module cannot apply published changes yet (args <c>type</c>, <c>module</c>).</summary>
    public const string PublishNotSupported = "config.publish.not_supported";

    /// <summary>The publish failed and was rolled back; nothing was applied (args <c>reason</c>).</summary>
    public const string PublishFailed = "config.publish.failed";

    /// <summary><c>config.licence.quota_exceeded</c>: the draft would take the site over its licence (LIC hook).</summary>
    public const string LicenceQuotaExceeded = "config.licence.quota_exceeded";

    /// <summary><c>config.secret.invalid</c>: a <c>$secret</c> value is not a string.</summary>
    public const string SecretInvalid = "config.secret.invalid";

    /// <summary>Fix: remove the field (<c>config.fix.remove_field</c>).</summary>
    public const string FixRemoveField = "config.fix.remove_field";

    /// <summary>Fix: set the default value (<c>config.fix.set_default</c>).</summary>
    public const string FixSetDefault = "config.fix.set_default";

    /// <summary>Fix: set the nearest allowed value (<c>config.fix.set_nearest</c>).</summary>
    public const string FixSetNearest = "config.fix.set_nearest";

    /// <summary>Every code a draft or a validation can produce, for the translation scan (CFG-U-018).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DraftNotFound, DraftNotOpen, DraftTooLarge, EntryInvalid, EntryOutdated, ReferenceMissing, ReferenceInUse, PublishInvalid, PublishConflict,
        PublishInProgress, ApprovalRequired, ApprovalSelfForbidden, LockHeldBy, LockNotSupported, ImportInvalidBundle, ImportUnmappedReference,
        VersionNotFound, LicenceQuotaExceeded, SecretInvalid, FixRemoveField, FixSetDefault, FixSetNearest, PublishNotSupported, PublishFailed,
        "api.client_kind.not_allowed",
    ];

    /// <summary>The draft does not exist, or the user may not see it.</summary>
    /// <param name="id">The draft id.</param>
    /// <returns>The error.</returns>
    public static Error NotFound(Guid id) => Error.Create(ErrorKind.NotFound, DraftNotFound, ("draftId", id));

    /// <summary>The draft is not open for changes.</summary>
    /// <param name="id">The draft id.</param>
    /// <param name="status">The status.</param>
    /// <returns>The error.</returns>
    public static Error NotOpen(Guid id, string status) => Error.Create(ErrorKind.Unprocessable, DraftNotOpen, ("draftId", id), ("status", status));

    /// <summary>The draft has the most entries.</summary>
    /// <param name="max">The limit.</param>
    /// <returns>The error.</returns>
    public static Error TooLarge(int max) => Error.Create(ErrorKind.Unprocessable, DraftTooLarge, ("max", max));

    /// <summary>The draft was changed by someone else: the editor sent an old version.</summary>
    /// <param name="expected">The version the editor had.</param>
    /// <param name="actual">The current version.</param>
    /// <returns>The error (<c>common.version.conflict</c>).</returns>
    public static Error VersionConflict(long expected, long actual) =>
        Error.Create(ErrorKind.PreconditionFailed, CommonErrorCodes.VersionConflict.Value, ("expected", expected), ("actual", actual));

    /// <summary>The user may not do this.</summary>
    /// <param name="permission">The missing permission.</param>
    /// <returns>The error.</returns>
    public static Error Forbidden(string permission) => Error.Create(ErrorKind.Forbidden, CommonErrorCodes.Forbidden.Value, ("permission", permission));

    /// <summary>The configuration is not available from this client kind.</summary>
    /// <param name="required">The required client kind.</param>
    /// <returns>The error.</returns>
    public static Error ClientKindNotAllowed(string required) =>
        Error.Create(ErrorKind.Forbidden, "api.client_kind.not_allowed", ("required", required));
}
