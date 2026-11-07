using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Model.Documents;

/// <summary>
/// A semantic version range supporting MAJOR.MINOR.PATCH, ^x.y, and *.
/// </summary>
public sealed record SemVersionRange
{
    public string Raw { get; }
    private readonly bool _isAny;
    private readonly bool _isCaret;
    private readonly int _minMajor;
    private readonly int _minMinor;
    private readonly int _minPatch;

    private SemVersionRange(string raw, bool isAny, bool isCaret, int minMajor, int minMinor, int minPatch)
    {
        Raw = raw;
        _isAny = isAny;
        _isCaret = isCaret;
        _minMajor = minMajor;
        _minMinor = minMinor;
        _minPatch = minPatch;
    }

    public static Result<SemVersionRange> Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CommonErrors.InvalidFormat("SemVersionRange");
        }

        string trimmed = input.Trim();
        if (trimmed == "*")
        {
            return new SemVersionRange(trimmed, isAny: true, isCaret: false, 0, 0, 0);
        }

        bool isCaret = trimmed.StartsWith('^');
        string versionPart = isCaret ? trimmed[1..] : trimmed;

        string[] parts = versionPart.Split('.');
        if (parts.Length < 1 || parts.Length > 3)
        {
            return CommonErrors.InvalidFormat("SemVersionRange");
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major < 0)
        {
            return CommonErrors.InvalidFormat("SemVersionRange major");
        }

        int minor = 0;
        if (parts.Length >= 2)
        {
            if (parts[1] is "*" or "x" or "X")
            {
                minor = 0;
            }
            else if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) || minor < 0)
            {
                return CommonErrors.InvalidFormat("SemVersionRange minor");
            }
        }

        int patch = 0;
        if (parts.Length == 3)
        {
            if (parts[2] is "*" or "x" or "X")
            {
                patch = 0;
            }
            else if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out patch) || patch < 0)
            {
                return CommonErrors.InvalidFormat("SemVersionRange patch");
            }
        }

        return new SemVersionRange(trimmed, isAny: false, isCaret, major, minor, patch);
    }

    public static bool TryParse(string? input, [NotNullWhen(true)] out SemVersionRange? range)
    {
        var result = Parse(input);
        if (result.IsSuccess)
        {
            range = result.Value;
            return true;
        }

        range = null;
        return false;
    }

    public bool Satisfies(SemVersion version)
    {
        if (_isAny) return true;

        if (_isCaret)
        {
            if (_minMajor > 0)
            {
                if (version.Major != _minMajor) return false;
                if (version.Minor < _minMinor) return false;
                if (version.Minor == _minMinor && version.Patch < _minPatch) return false;
                return true;
            }
            else if (_minMinor > 0)
            {
                if (version.Major != 0 || version.Minor != _minMinor) return false;
                return version.Patch >= _minPatch;
            }
            else
            {
                return version.Major == 0 && version.Minor == 0 && version.Patch == _minPatch;
            }
        }

        if (version.Major != _minMajor) return false;
        if (version.Minor != _minMinor) return false;
        if (version.Patch != _minPatch) return false;
        return true;
    }

    public override string ToString() => Raw;
}
