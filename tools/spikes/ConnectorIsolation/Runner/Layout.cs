namespace Watchpoint.Spikes.ConnectorIsolation.Runner;

/// <summary>Paths of the sibling build outputs (same configuration as the runner).</summary>
internal static class Layout
{
    private static readonly string _base = AppContext.BaseDirectory;                       // …/Runner/bin/<cfg>/<tfm>/
    private static readonly string _tfm = new DirectoryInfo(_base).Name;
    private static readonly string _configuration = new DirectoryInfo(_base).Parent!.Name;
    private static readonly string _root = Path.GetFullPath(Path.Combine(_base, "..", "..", "..", ".."));

    public static string ConnectorAssembly =>
        Path.Combine(_root, "Connector", "bin", _configuration, _tfm, "Watchpoint.Spikes.ConnectorIsolation.Connector.dll");

    public static string HostExecutable =>
        Path.Combine(_root, "Host", "bin", _configuration, _tfm, "Watchpoint.Spikes.ConnectorIsolation.Host");

    public static string RunnerExecutable => Path.Combine(_base, "Watchpoint.Spikes.ConnectorIsolation.Runner");
}
