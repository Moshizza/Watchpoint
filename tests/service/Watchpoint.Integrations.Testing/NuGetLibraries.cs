namespace Watchpoint.Integrations.Testing;

/// <summary>Two versions of a real NuGet library (Humanizer.Core), for packages that must get their own copy (IRT-I-004).</summary>
public static class NuGetLibraries
{
    /// <summary>The assembly name of both versions.</summary>
    public const string HumanizerAssembly = "Humanizer";

    /// <summary>Gets the bytes of Humanizer 2.14.1.</summary>
    public static byte[] Humanizer2 => Read("Humanizer-2.14.1.dll");

    /// <summary>Gets the bytes of Humanizer 3.0.1.</summary>
    public static byte[] Humanizer3 => Read("Humanizer-3.0.1.dll");

    private static byte[] Read(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "nuget-libraries", file);
        return File.Exists(path) ? File.ReadAllBytes(path) : throw new FileNotFoundException($"{file} was not copied to the test output.", path);
    }
}
