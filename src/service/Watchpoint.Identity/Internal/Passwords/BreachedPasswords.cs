using System.IO.Compression;
using System.Text;

namespace Watchpoint.Identity.Internal.Passwords;

/// <summary>Loads and checks against the embedded 100,000 most common breached passwords (spec IDN §5).</summary>
internal static class BreachedPasswords
{
    private static readonly Lazy<HashSet<string>> _passwords = new(LoadPasswords);

    public static bool IsBreached(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        return _passwords.Value.Contains(password.Trim().ToLowerInvariant());
    }

    private static HashSet<string> LoadPasswords()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(BreachedPasswords).Assembly;
        const string resourceName = "Watchpoint.Identity.Internal.Passwords.breached-passwords-100k.txt.gz";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found in {assembly.FullName}.");

        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                set.Add(trimmed);
            }
        }

        return set;
    }
}
