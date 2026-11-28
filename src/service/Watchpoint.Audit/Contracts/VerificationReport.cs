namespace Watchpoint.Audit.Contracts;

/// <summary>Result of verifying a segment of a chain.</summary>
/// <param name="ChainId">The chain.</param>
/// <param name="From">First sequence verified.</param>
/// <param name="To">Last sequence verified.</param>
/// <param name="RecordsChecked">Number of records checked.</param>
/// <param name="Valid"><see langword="true"/> when there is no issue.</param>
/// <param name="Issues">The issues found.</param>
/// <param name="At">When the verification ran.</param>
public sealed record VerificationReport(string ChainId, long From, long To, long RecordsChecked, bool Valid, IReadOnlyList<VerificationIssue> Issues, DateTimeOffset At);

/// <summary>One integrity problem.</summary>
/// <param name="Kind">The kind of problem.</param>
/// <param name="Sequence">The sequence it was found at.</param>
/// <param name="Message">A developer-readable description.</param>
public sealed record VerificationIssue(VerificationIssueKind Kind, long Sequence, string Message);

/// <summary>Kinds of integrity problems.</summary>
public enum VerificationIssueKind
{
    /// <summary>A stored hash or previous hash does not match the record.</summary>
    HashMismatch,

    /// <summary>A sequence number is missing.</summary>
    SequenceGap,

    /// <summary>A checkpoint does not match the record it covers, or nothing anchors the start of the chain.</summary>
    CheckpointMismatch,

    /// <summary>A checkpoint signature is not valid.</summary>
    CheckpointSignatureInvalid,

    /// <summary>Records covered by a checkpoint are missing at the end of the chain (truncation).</summary>
    MissingAfterCheckpoint,
}
