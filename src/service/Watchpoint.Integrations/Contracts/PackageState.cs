namespace Watchpoint.Integrations.Contracts;

/// <summary>State of a package version in the catalogue (spec IRT §4.2).</summary>
public enum PackageState
{
    /// <summary>Discovered, not verified yet.</summary>
    Found,

    /// <summary>A verification step failed; see <see cref="PackageInfo.Problems"/>.</summary>
    Invalid,

    /// <summary>All checks passed; types not registered yet.</summary>
    Verified,

    /// <summary>Types registered with the type catalogue; connections may use it.</summary>
    Active,

    /// <summary>Disabled by a user: its connections are stopped, its types stay registered.</summary>
    Disabled,

    /// <summary>The file is gone and no connection uses the version.</summary>
    Removed,
}
