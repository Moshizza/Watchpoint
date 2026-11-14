using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>
/// The real <c>nats-server</c> binary for the bundled-NATS tests: the official release archive, pinned by version
/// and SHA-256 (values from the release's <c>SHA256SUMS</c>; the same pins are in <c>deploy/versions.env</c>),
/// downloaded once per machine into the temp folder. nats-server is licensed under Apache-2.0.
/// </summary>
internal static class NatsServerBinary
{
    public const string Version = "2.15.2";

    private static readonly Dictionary<string, (string Asset, string Sha256)> _archives = new(StringComparer.Ordinal)
    {
        ["linux-x64"] = ("linux-amd64.tar.gz", "67cb8e10bc6f0c4b47f75a728529a20953dbae2a4f9a4a52ae8517d65ae303ff"),
        ["linux-arm64"] = ("linux-arm64.tar.gz", "bc2c788dcbf2849af749564e06a0d48c19bced851f2d68a2e21c54d078949b28"),
        ["win-x64"] = ("windows-amd64.zip", "f6398d5d11093778f870db70242cb50d20a58134c937c805ba8d114e7e398766"),
        ["win-arm64"] = ("windows-arm64.zip", "0c688b55a074b48289b84edc8b68936421c00cef7bb5ae589ef48e5f8e6a161d"),
        ["osx-x64"] = ("darwin-amd64.tar.gz", "9c45b23c19498692e81cd39c75e8382f0e6ff59c0d5b9470e32fe6640afa4446"),
        ["osx-arm64"] = ("darwin-arm64.tar.gz", "cbd2219d01502c38733485ac684269da585ee479fa3b1377264beecdefdf9e9d"),
    };

    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The pinned archives: asset suffix → SHA-256.</summary>
    public static IReadOnlyDictionary<string, string> PinnedArchives => _archives.Values.ToDictionary(a => a.Asset, a => a.Sha256);

    public static string FileName => OperatingSystem.IsWindows() ? "nats-server.exe" : "nats-server";

    /// <summary>
    /// Returns the path of the verified binary. Without network access the test is skipped locally and fails in CI.
    /// </summary>
    public static async Task<string> GetAsync(CancellationToken ct)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var rid = $"{os}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
        if (!_archives.TryGetValue(rid, out var archive))
        {
            Assert.Skip($"No pinned nats-server {Version} archive for {rid}.");
        }

        var folder = Path.Combine(Path.GetTempPath(), "watchpoint-test-binaries", $"nats-server-v{Version}-{rid}");
        var binary = Path.Combine(folder, FileName);
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(binary))
            {
                return binary;
            }

            byte[] content;
            var url = $"https://github.com/nats-io/nats-server/releases/download/v{Version}/nats-server-v{Version}-{archive.Asset}";
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                content = await http.GetByteArrayAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && !TestTimeouts.IsCi)
            {
                Assert.Skip($"nats-server {Version} could not be downloaded ({ex.Message}).");
                throw;
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(content));
            if (actual != archive.Sha256)
            {
                throw new InvalidOperationException($"Checksum mismatch for {url}: expected {archive.Sha256}, got {actual}.");
            }

            // Extract only the server binary, to a temporary name first: another test process may do the same.
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
            await ExtractAsync(content, archive.Asset, temp, ct);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.Move(temp, binary, overwrite: true);
            return binary;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Copies the binary into the <c>bin</c> folder of a node's data folder (the tests' <c>Paths:Binaries</c>).</summary>
    public static async Task<string> InstallAsync(string binariesFolder, CancellationToken ct)
    {
        var source = await GetAsync(ct);
        Directory.CreateDirectory(binariesFolder);
        var target = Path.Combine(binariesFolder, FileName);
        File.Copy(source, target, overwrite: true);
        return target;
    }

    private static async Task ExtractAsync(byte[] archive, string asset, string target, CancellationToken ct)
    {
        using var input = new MemoryStream(archive);
        if (asset.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            var entry = zip.Entries.Single(e => e.Name == FileName);
            await using var source = entry.Open();
            await using var output = File.Create(target);
            await source.CopyToAsync(output, ct);
            return;
        }

        await using var gzip = new GZipStream(input, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType == TarEntryType.RegularFile && Path.GetFileName(entry.Name) == FileName && entry.DataStream is { } data)
            {
                await using var output = File.Create(target);
                await data.CopyToAsync(output, ct);
                return;
            }
        }

        throw new InvalidOperationException($"{FileName} not found in {asset}.");
    }
}
