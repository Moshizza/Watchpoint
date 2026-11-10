using Watchpoint.Model.Documents.Library;

namespace Watchpoint.Model.Contracts;

/// <summary>
/// The documents of the standard library, as a dependency for the cross-document validation of packages
/// (spec SCM §7, IRT §4.3 step 6).
/// </summary>
public interface IStandardLibrary
{
    /// <summary>Gets the standard library folder (capabilities, types, translations, symbol ids; its files are not on disk).</summary>
    LibraryFolder Folder { get; }
}
