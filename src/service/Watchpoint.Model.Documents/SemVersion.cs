using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents;

/// <summary>
/// A semantic version (MAJOR.MINOR.PATCH with optional prerelease and build metadata).
/// </summary>
public sealed partial record SemVersion : IComparable<SemVersion>, IComparable
{
    private static readonly Regex _semVerRegex = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.Compiled);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? Prerelease { get; }
    public string? BuildMetadata { get; }

    public SemVersion(int major, int minor, int patch, string? prerelease = null, string? buildMetadata = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        BuildMetadata = buildMetadata;
    }

    public static Result<SemVersion> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CommonErrors.InvalidFormat("SemVer MAJOR.MINOR.PATCH");
        }

        var match = _semVerRegex.Match(input);
        if (!match.Success)
        {
            return CommonErrors.InvalidFormat("SemVer MAJOR.MINOR.PATCH");
        }

        int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int patch = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        string? prerelease = match.Groups[4].Success ? match.Groups[4].Value : null;
        string? build = match.Groups[5].Success ? match.Groups[5].Value : null;

        return new SemVersion(major, minor, patch, prerelease, build);
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out SemVersion? version)
    {
        var result = Parse(input);
        if (result.IsSuccess)
        {
            version = result.Value;
            return true;
        }

        version = null;
        return false;
    }

    public int CompareTo(SemVersion? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other is null) return 1;

        int majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0) return majorComparison;

        int minorComparison = Minor.CompareTo(other.Minor);
        if (minorComparison != 0) return minorComparison;

        int patchComparison = Patch.CompareTo(other.Patch);
        if (patchComparison != 0) return patchComparison;

        if (Prerelease == other.Prerelease) return 0;
        if (Prerelease is null) return 1;
        if (other.Prerelease is null) return -1;

        return string.CompareOrdinal(Prerelease, other.Prerelease);
    }

    public int CompareTo(object? obj)
    {
        if (obj is null) return 1;
        if (obj is SemVersion other) return CompareTo(other);
        throw new ArgumentException("Object must be of type SemVersion", nameof(obj));
    }

    public static bool operator <(SemVersion? left, SemVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(SemVersion? left, SemVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(SemVersion? left, SemVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator >=(SemVersion? left, SemVersion? right) =>
        left is null ? right is null : left.CompareTo(right) >= 0;

    public override string ToString()
    {
        var result = $"{Major.ToString(CultureInfo.InvariantCulture)}.{Minor.ToString(CultureInfo.InvariantCulture)}.{Patch.ToString(CultureInfo.InvariantCulture)}";
        if (Prerelease != null)
        {
            result += $"-{Prerelease}";
        }
        if (BuildMetadata != null)
        {
            result += $"+{BuildMetadata}";
        }
        return result;
    }
}
