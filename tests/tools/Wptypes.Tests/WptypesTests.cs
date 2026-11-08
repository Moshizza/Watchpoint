using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Watchpoint.Wptypes.Tests;

[Trait("layer", "U")]
[Trait("spec", "SCM")]
public sealed class WptypesTests : IDisposable
{
    private static readonly string _fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private readonly string _temp = Directory.CreateTempSubdirectory("wptypes-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void SCM_U_037_Standard_library_v0_is_valid()
    {
        var (exit, output, _) = Run("validate", RepoFolder("types"), "--strict-i18n");

        exit.ShouldBe(WptypesCli.Ok, output);
        output.ShouldContain("15 capabilities, 8 types, 0 errors");
    }

    [Fact]
    public void SCM_U_038_Wptypes_exit_codes()
    {
        // The small fixture lacks fr/ar translations and symbols: errors with --strict-i18n.
        var (errors, json, _) = Run("validate", Path.Combine(_fixtures, "small"), "--with", RepoFolder("types"), "--strict-i18n", "--format", "json");
        var (badPath, _, message) = Run("validate", Path.Combine(_temp, "missing"));
        var (badUsage, _, _) = Run("frobnicate", _temp);

        errors.ShouldBe(WptypesCli.Errors);
        badPath.ShouldBe(WptypesCli.Usage);
        message.ShouldContain("folder not found");
        badUsage.ShouldBe(WptypesCli.Usage);

        using var report = JsonDocument.Parse(json);
        report.RootElement.GetProperty("errors").GetInt32().ShouldBeGreaterThan(0);
        var problems = report.RootElement.GetProperty("problems").EnumerateArray().ToList();
        problems.Count.ShouldBe(report.RootElement.GetProperty("errors").GetInt32() + report.RootElement.GetProperty("warnings").GetInt32());
        problems.ShouldContain(p => p.GetProperty("code").GetString() == "scm.symbol.missing" && p.GetProperty("file").GetString() == "device-types/test.gadget.json");
        problems.ShouldContain(p => p.GetProperty("code").GetString() == "scm.i18n.key_missing" && p.GetProperty("args").GetProperty("culture").GetString() == "fr");
    }

    [Fact]
    public void SCM_U_039_Wptypes_doc_output_stable()
    {
        var file = Path.Combine(_temp, "catalogue.md");

        var (exit, _, _) = Run("doc", Path.Combine(_fixtures, "small"), "--with", RepoFolder("types"), "--out", file);

        exit.ShouldBe(WptypesCli.Ok);
        File.ReadAllText(file).ShouldBe(File.ReadAllText(Path.Combine(_fixtures, "small.catalogue.md")).ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task SCM_U_040_Wptypes_gen_ts_compiles()
    {
        var (exit, _, _) = Run("gen-ts", RepoFolder("types"), "--out", _temp);
        exit.ShouldBe(WptypesCli.Ok);
        var file = Path.Combine(_temp, "watchpoint-types.ts");
        var source = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        source.ShouldContain("export type TypeId = ");
        source.ShouldContain("readonly position: 'closed' | 'open' | 'heldOpen' | 'forced' | 'unknown';");

        var npx = FindOnPath(OperatingSystem.IsWindows() ? "npx.cmd" : "npx");
        if (npx is null)
        {
            Assert.Skip("Node.js (npx) not available");
        }

        var info = new ProcessStartInfo(npx, ["-y", "-p", "typescript@6.0.3", "tsc", "--noEmit", "--strict", "--target", "es2022", "--module", "esnext", file])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _temp,
        };
        using var tsc = Process.Start(info)!;
        var stdout = tsc.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = tsc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await tsc.WaitForExitAsync(TestContext.Current.CancellationToken);

        tsc.ExitCode.ShouldBe(0, await stdout + await stderr);
    }

    private static (int Exit, string Output, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = WptypesCli.Run(args, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static string RepoFolder(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, name, "capabilities")))
            {
                return Path.Combine(dir.FullName, name);
            }
        }

        throw new DirectoryNotFoundException(name);
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, program)).FirstOrDefault(File.Exists);
}
