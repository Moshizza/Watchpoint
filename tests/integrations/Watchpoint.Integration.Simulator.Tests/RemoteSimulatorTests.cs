using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Watchpoint.Wpsim;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class RemoteSimulatorTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    [Fact]
    public async Task SIM_I_002_Remote_mode_against_wpsim()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = WpsimApp.Build(0);
        await app.StartAsync(ct);

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.First()!;
            var wsUri = address.Replace("http://", "ws://") + "/sim";

            var time = new FakeTimeProvider();
            var json = JsonSerializer.SerializeToElement(new
            {
                mode = "remote",
                url = wsUri,
            });

            var context = new TestConnectorContext(json, time);
            var connector = new SimulatorConnector();
            await connector.InitializeAsync(context, ct);
            await connector.StartAsync(ct);

            await Task.Delay(TimeSpan.FromMilliseconds(200), Clock, ct);

            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var injectResp = await client.PostAsJsonAsync("/control/inject", new
            {
                device = "door-0001",
                props = new Dictionary<string, string> { ["doorPosition.position"] = "open" },
            }, ct);
            injectResp.IsSuccessStatusCode.ShouldBeTrue();

            await Task.Delay(TimeSpan.FromMilliseconds(200), Clock, ct);

            context.Sink.Properties.Any(p => p.Device.NativeId == "door-0001" && p.PropertyPath == "doorPosition.position").ShouldBeTrue();

            await connector.StopAsync(ct);
            await connector.DisposeAsync();
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task SIM_I_003_Remote_disconnect_and_catch_up()
    {
        var ct = TestContext.Current.CancellationToken;
        var app = WpsimApp.Build(0);
        await app.StartAsync(ct);

        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = addresses?.First()!;
            var wsUri = address.Replace("http://", "ws://") + "/sim";

            var time = new FakeTimeProvider();
            var json = JsonSerializer.SerializeToElement(new
            {
                mode = "remote",
                url = wsUri,
            });

            var context = new TestConnectorContext(json, time);
            var connector = new SimulatorConnector();
            await connector.InitializeAsync(context, ct);
            await connector.StartAsync(ct);

            await Task.Delay(TimeSpan.FromMilliseconds(150), Clock, ct);

            using var client = new HttpClient { BaseAddress = new Uri(address) };

            await client.PostAsync("/control/drop", null, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(150), Clock, ct);

            for (int i = 1; i <= 5; i++)
            {
                await client.PostAsJsonAsync("/control/inject", new
                {
                    device = "door-0001",
                    @event = $"test.ev{i}",
                }, ct);
            }

            // Create a new connector instance simulating reconnect
            var reconnected = new SimulatorConnector();
            await reconnected.InitializeAsync(context, ct);
            await reconnected.StartAsync(ct);
            await Task.Delay(TimeSpan.FromMilliseconds(200), Clock, ct);

            await reconnected.CatchUpAsync(ct);
            await Task.Delay(TimeSpan.FromMilliseconds(250), Clock, ct);

            for (int i = 1; i <= 5; i++)
            {
                context.Sink.Events.Any(e => e.EventPath == $"test.ev{i}").ShouldBeTrue();
            }

            await reconnected.StopAsync(ct);
            await reconnected.DisposeAsync();

            await connector.StopAsync(ct);
            await connector.DisposeAsync();
        }
        finally
        {
            await app.StopAsync(ct);
            await app.DisposeAsync();
        }
    }
}
