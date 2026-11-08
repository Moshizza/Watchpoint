using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Shouldly;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Host;

[Trait("layer", "I")]
[Trait("spec", "HST")]
public sealed class HostIntegrationTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task HST_I_001_Host_starts_with_compose_infrastructure()
    {
        await postgres.StartAsync(Ct);
        await nats.StartAsync(Ct);

        var mainPort = GetFreePort();
        var mgmtPort = GetFreePort();

        var hostDll = Path.Combine(AppContext.BaseDirectory, "Watchpoint.Host.dll");
        if (!File.Exists(hostDll))
        {
            var slnRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            hostDll = Path.Combine(slnRoot, "src/service/Watchpoint.Host/bin/Release/net10.0/Watchpoint.Host.dll");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "wp_i001_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "watchpoint.dev.json");

        var config = new JsonObject
        {
            ["Node"] = new JsonObject
            {
                ["Environment"] = "Development",
                ["Name"] = "test-node"
            },
            ["Http"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mainPort}" }
            },
            ["Management"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mgmtPort}" }
            },
            ["Paths"] = new JsonObject
            {
                ["Data"] = tempDir,
                ["Logs"] = Path.Combine(tempDir, "logs"),
                ["Packages"] = Path.Combine(tempDir, "packages"),
                ["Binaries"] = Path.Combine(tempDir, "bin")
            },
            ["Nats"] = new JsonObject
            {
                ["Mode"] = "External",
                ["Url"] = nats.Url
            },
            ["Database"] = new JsonObject
            {
                ["Stores"] = new JsonObject { ["Configuration"] = "default" },
                ["Databases"] = new JsonObject
                {
                    ["default"] = new JsonObject
                    {
                        ["Host"] = "127.0.0.1",
                        ["Port"] = 5432
                    }
                }
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), Ct);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --config=\"{configPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            process.Start();

            using var httpClient = new HttpClient();
            var readyUrl = $"http://127.0.0.1:{mainPort}/health/ready";

            var sw = Stopwatch.StartNew();
            var isReady = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(30) && !process.HasExited)
            {
                try
                {
                    var resp = await httpClient.GetAsync(readyUrl, Ct);
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        var text = await resp.Content.ReadAsStringAsync(Ct);
                        if (text.Contains("\"ready\""))
                        {
                            isReady = true;
                            break;
                        }
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
            }

            isReady.ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(Ct);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HST_I_002_Host_reports_not_ready_without_database()
    {
        var mainPort = GetFreePort();
        var mgmtPort = GetFreePort();
        var closedDbPort = GetFreePort();

        var hostDll = Path.Combine(AppContext.BaseDirectory, "Watchpoint.Host.dll");
        if (!File.Exists(hostDll))
        {
            var slnRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            hostDll = Path.Combine(slnRoot, "src/service/Watchpoint.Host/bin/Release/net10.0/Watchpoint.Host.dll");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "wp_i002_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "watchpoint.dev.json");

        var config = new JsonObject
        {
            ["Node"] = new JsonObject
            {
                ["Environment"] = "Development",
                ["Name"] = "test-unreachable-db"
            },
            ["Http"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mainPort}" }
            },
            ["Management"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mgmtPort}" }
            },
            ["Paths"] = new JsonObject
            {
                ["Data"] = tempDir,
                ["Logs"] = Path.Combine(tempDir, "logs"),
                ["Packages"] = Path.Combine(tempDir, "packages"),
                ["Binaries"] = Path.Combine(tempDir, "bin")
            },
            ["Database"] = new JsonObject
            {
                ["StartupWait"] = "PT2S",
                ["Stores"] = new JsonObject { ["Configuration"] = "default" },
                ["Databases"] = new JsonObject
                {
                    ["default"] = new JsonObject
                    {
                        ["Host"] = "127.0.0.1",
                        ["Port"] = closedDbPort
                    }
                }
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), Ct);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --config=\"{configPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            process.Start();

            using var httpClient = new HttpClient();
            var sw = Stopwatch.StartNew();
            var reported503WithDatabase = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(5) && !process.HasExited)
            {
                try
                {
                    var resp = await httpClient.GetAsync($"http://127.0.0.1:{mainPort}/health/ready", Ct);
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        var content = await resp.Content.ReadAsStringAsync(Ct);
                        if (content.Contains("database"))
                        {
                            reported503WithDatabase = true;
                            break;
                        }
                    }
                }
                catch
                {
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), TimeProvider.System, Ct);
            }

            await process.WaitForExitAsync(Ct);
            process.ExitCode.ShouldBe(75);
            reported503WithDatabase.ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(Ct);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HST_I_003_Management_endpoints_only_on_management_port()
    {
        var mainPort = GetFreePort();
        var mgmtPort = GetFreePort();

        var hostDll = Path.Combine(AppContext.BaseDirectory, "Watchpoint.Host.dll");
        if (!File.Exists(hostDll))
        {
            var slnRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            hostDll = Path.Combine(slnRoot, "src/service/Watchpoint.Host/bin/Release/net10.0/Watchpoint.Host.dll");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "wp_i003_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "watchpoint.dev.json");

        var config = new JsonObject
        {
            ["Node"] = new JsonObject
            {
                ["Environment"] = "Development",
                ["Name"] = "test-mgmt-ports"
            },
            ["Http"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mainPort}" }
            },
            ["Management"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mgmtPort}" }
            },
            ["Paths"] = new JsonObject
            {
                ["Data"] = tempDir,
                ["Logs"] = Path.Combine(tempDir, "logs"),
                ["Packages"] = Path.Combine(tempDir, "packages"),
                ["Binaries"] = Path.Combine(tempDir, "bin")
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), Ct);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --config=\"{configPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        try
        {
            process.Start();

            using var httpClient = new HttpClient();
            var sw = Stopwatch.StartNew();
            var mgmtResponded = false;
            while (sw.Elapsed < TimeSpan.FromSeconds(15) && !process.HasExited)
            {
                try
                {
                    var resp = await httpClient.GetAsync($"http://127.0.0.1:{mgmtPort}/metrics", Ct);
                    if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        mgmtResponded = true;
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, Ct);
            }

            mgmtResponded.ShouldBeTrue();

            // Check main port returns 404
            var mainResp = await httpClient.GetAsync($"http://127.0.0.1:{mainPort}/metrics", Ct);
            mainResp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(Ct);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HST_I_004_systemd_notify()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.Skip("systemd notify is only supported on Linux");
            return;
        }

        var mainPort = GetFreePort();
        var mgmtPort = GetFreePort();

        var socketPath = Path.Combine(Path.GetTempPath(), $"notify_{Guid.NewGuid():N}.sock");
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        using var serverSocket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        serverSocket.Bind(endpoint);

        var hostDll = Path.Combine(AppContext.BaseDirectory, "Watchpoint.Host.dll");
        if (!File.Exists(hostDll))
        {
            var slnRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            hostDll = Path.Combine(slnRoot, "src/service/Watchpoint.Host/bin/Release/net10.0/Watchpoint.Host.dll");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "wp_i004_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "watchpoint.dev.json");

        var config = new JsonObject
        {
            ["Node"] = new JsonObject
            {
                ["Environment"] = "Development",
                ["Name"] = "test-systemd-notify"
            },
            ["Http"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mainPort}" }
            },
            ["Management"] = new JsonObject
            {
                ["Urls"] = new JsonArray { $"http://127.0.0.1:{mgmtPort}" }
            },
            ["Paths"] = new JsonObject
            {
                ["Data"] = tempDir,
                ["Logs"] = Path.Combine(tempDir, "logs"),
                ["Packages"] = Path.Combine(tempDir, "packages"),
                ["Binaries"] = Path.Combine(tempDir, "bin")
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), Ct);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{hostDll}\" --config=\"{configPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.EnvironmentVariables["NOTIFY_SOCKET"] = socketPath;

        try
        {
            process.Start();

            var buffer = new byte[1024];
            var sw = Stopwatch.StartNew();
            var readyReceived = false;

            while (sw.Elapsed < TimeSpan.FromSeconds(15) && !process.HasExited)
            {
                if (serverSocket.Poll(500_000, SelectMode.SelectRead))
                {
                    var received = serverSocket.Receive(buffer);
                    var msg = System.Text.Encoding.UTF8.GetString(buffer, 0, received);
                    if (msg.Contains("READY=1"))
                    {
                        readyReceived = true;

                        // READY=1 is sent only after Edge: readiness must already be 200.
                        using var readyClient = new HttpClient();
                        var readyResp = await readyClient.GetAsync($"http://127.0.0.1:{mgmtPort}/health/ready", Ct);
                        readyResp.StatusCode.ShouldBe(HttpStatusCode.OK);
                        break;
                    }
                }
            }

            readyReceived.ShouldBeTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync(Ct);
            }
            serverSocket.Close();
            if (File.Exists(socketPath))
            {
                File.Delete(socketPath);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
