using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class ConnectorBaseTests
{
    private sealed class FakeConnectorContext : IConnectorContext
    {
        public ConnectionInfo Connection { get; } = new(Guid.NewGuid(), "TestConn", "pkg.test", "1.0", "testServer", "en-US");
        public System.Text.Json.JsonElement Settings => default;
        public T GetSettings<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) => throw new NotImplementedException();
        public ISecretAccessor Secrets => throw new NotImplementedException();
        public IObservationSink Sink => FakeSink;
        public IDeviceMap Devices => throw new NotImplementedException();
        public IConnectionStore Store => throw new NotImplementedException();
        public IConnectorScheduler Scheduler => throw new NotImplementedException();
        public IRequestGate Gate => throw new NotImplementedException();
        public IConnectionHealth Health => FakeHealth;
        public ISecureHttp Http => throw new NotImplementedException();
        public IInboundEndpoints Inbound => throw new NotImplementedException();
        public IConnectorMetrics Metrics => throw new NotImplementedException();
        public ILoggerFactory Logging { get; } = NullLoggerFactory.Instance;
        public TimeProvider Time { get; init; } = TimeProvider.System;
        public CancellationToken Stopping => StoppingCts.Token;

        public CancellationTokenSource StoppingCts { get; } = new();
        public FakeHealthReport FakeHealth { get; } = new();
        public FakeObservationSink FakeSink { get; } = new();
    }

    private sealed class FakeHealthReport : IConnectionHealth
    {
        public List<(ConnectionStatus Status, ConnectorMessage? Reason)> Reports { get; } = [];

        public void Report(ConnectionStatus status, ConnectorMessage? reason = null)
        {
            lock (Reports)
            {
                Reports.Add((status, reason));
            }
        }
    }

    private sealed class FakeObservationSink : IObservationSink
    {
        public List<(string ServerId, Connectivity Connectivity, string? Reason)> ServerHealthReports { get; } = [];

        public void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null) { }
        public void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null, DateTimeOffset? sourceTime = null) { }
        public void ReportInventoryChanged(InventorySnapshot partialOrComplete) { }
        public void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null) { }
        public void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good, DateTimeOffset? sourceTime = null) { }

        public void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null)
        {
            lock (ServerHealthReports)
            {
                ServerHealthReports.Add((serverNativeId, connectivity, reason));
            }
        }

        public ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct) => ValueTask.FromResult(new SinkFlushResult(0, 0, 0));
    }

    private sealed class TestConnector : ConnectorBase, IStateRefresh, IEventCatchUp
    {
        public int ConnectAttempts { get; private set; }
        public int FailuresBeforeSuccess { get; set; }
        public int DisconnectCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int CatchUpCalls { get; private set; }
        public TaskCompletionSource ConnectedTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override ValueTask ConnectOnceAsync(CancellationToken ct)
        {
            ConnectAttempts++;
            if (ConnectAttempts <= FailuresBeforeSuccess)
            {
                throw new SocketException((int)SocketError.ConnectionRefused);
            }
            return ValueTask.CompletedTask;
        }

        protected override async ValueTask RunConnectedAsync(CancellationToken ct)
        {
            ConnectedTcs.TrySetResult();
            var tcs = new TaskCompletionSource();
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }

        protected override ValueTask DisconnectAsync(CancellationToken ct)
        {
            DisconnectCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshAsync(RefreshScope scope, CancellationToken ct)
        {
            RefreshCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CatchUpAsync(CancellationToken ct)
        {
            CatchUpCalls++;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct) =>
            ValueTask.FromResult(ConnectionTestResult.Succeeded(new ServerIdentity("TestModel", "1.0", null, new Dictionary<string, string>())));

        public override ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct) =>
            ValueTask.FromResult(new InventorySnapshot([], [], false));
    }

    [Fact]
    public async Task INT_U_011_ConnectorBase_StartAsync_returns_immediately_loop_connects()
    {
        var clock = TestTime.CreateProvider();
        var context = new FakeConnectorContext { Time = clock };
        var connector = new TestConnector { FailuresBeforeSuccess = 2 };

        await connector.InitializeAsync(context, TestContext.Current.CancellationToken);

        var startTask = connector.StartAsync(TestContext.Current.CancellationToken);
        startTask.IsCompleted.ShouldBeTrue();

        // Wait until 1st attempt has run and failed
        for (var i = 0; i < 50 && context.FakeHealth.Reports.Count < 2; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        context.FakeHealth.Reports.Count.ShouldBeGreaterThanOrEqualTo(2);
        context.FakeHealth.Reports[0].Status.ShouldBe(ConnectionStatus.Connecting);
        context.FakeHealth.Reports[1].Status.ShouldBe(ConnectionStatus.Disconnected);

        // Advance past 1st reconnect delay (1s ±20%)
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 50 && context.FakeHealth.Reports.Count < 4; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }
        context.FakeHealth.Reports.Count.ShouldBeGreaterThanOrEqualTo(4);
        context.FakeHealth.Reports[2].Status.ShouldBe(ConnectionStatus.Connecting);
        context.FakeHealth.Reports[3].Status.ShouldBe(ConnectionStatus.Disconnected);

        // Advance past 2nd reconnect delay (2s ±20%)
        clock.Advance(TimeSpan.FromSeconds(3));
        await connector.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), clock, TestContext.Current.CancellationToken);

        var statuses = context.FakeHealth.Reports.Select(r => r.Status).ToList();
        statuses.ShouldContain(ConnectionStatus.Connecting);
        statuses.ShouldContain(ConnectionStatus.Disconnected);
        statuses.Last().ShouldBe(ConnectionStatus.Connected);

        await connector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void INT_U_012_ConnectorBase_reconnect_delays()
    {
        var connector = new TestConnector();
        double[] baseSeconds = [1, 2, 4, 8, 16, 32, 60];

        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var expectedBase = baseSeconds[attempt - 1];
            var min = TimeSpan.FromSeconds(expectedBase * 0.79);
            var max = TimeSpan.FromSeconds(expectedBase * 1.21);

            for (var sample = 0; sample < 10; sample++)
            {
                var delay = connector.GetReconnectDelay(attempt);
                delay.ShouldBeGreaterThanOrEqualTo(min);
                delay.ShouldBeLessThanOrEqualTo(max);
            }
        }
    }

    [Fact]
    public async Task INT_U_013_ConnectorBase_OnConnected_calls_refresh_and_catch_up_when_implemented()
    {
        var clock = TestTime.CreateProvider();
        var context = new FakeConnectorContext { Time = clock };
        var connector = new TestConnector { FailuresBeforeSuccess = 0 };

        await connector.InitializeAsync(context, TestContext.Current.CancellationToken);
        await connector.StartAsync(TestContext.Current.CancellationToken);

        await connector.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), clock, TestContext.Current.CancellationToken);

        connector.RefreshCalls.ShouldBe(1);
        connector.CatchUpCalls.ShouldBe(1);

        await connector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task INT_U_014_ConnectorBase_disconnection_reports_root_server_health_Unknown_with_reason()
    {
        var clock = TestTime.CreateProvider();
        var context = new FakeConnectorContext { Time = clock };
        var connector = new TestConnector { FailuresBeforeSuccess = 1 };

        await connector.InitializeAsync(context, TestContext.Current.CancellationToken);
        await connector.StartAsync(TestContext.Current.CancellationToken);

        // First failure triggers disconnect
        for (var i = 0; i < 50 && context.FakeSink.ServerHealthReports.Count == 0; i++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), TimeProvider.System, TestContext.Current.CancellationToken);
        }

        context.FakeSink.ServerHealthReports.ShouldContain(r =>
            r.ServerId == "root" && r.Connectivity == Connectivity.Unknown && r.Reason == "connectionLost");

        await connector.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task INT_U_015_ConnectorBase_StopAsync_cancels_loop_and_calls_DisconnectAsync_once()
    {
        var clock = TestTime.CreateProvider();
        var context = new FakeConnectorContext { Time = clock };
        var connector = new TestConnector { FailuresBeforeSuccess = 0 };

        await connector.InitializeAsync(context, TestContext.Current.CancellationToken);
        await connector.StartAsync(TestContext.Current.CancellationToken);

        await connector.ConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), clock, TestContext.Current.CancellationToken);

        connector.DisconnectCalls.ShouldBe(0);

        await connector.StopAsync(TestContext.Current.CancellationToken);
        connector.DisconnectCalls.ShouldBe(1);

        // Calling Stop again should not invoke DisconnectAsync again
        await connector.DisposeAsync();
        connector.DisconnectCalls.ShouldBe(1);
    }

    [Fact]
    public void INT_U_016_ConnectorBase_exception_mapping_to_failure_categories()
    {
        var connector = new TestConnector();

        var (cat1, _) = connector.MapFailure(new SocketException((int)SocketError.HostUnreachable));
        cat1.ShouldBe(FailureCategory.Unreachable);

        var (cat2, _) = connector.MapFailure(new AuthenticationException("TLS failed"));
        cat2.ShouldBe(FailureCategory.TlsError);

        var (cat3, _) = connector.MapFailure(new TimeoutException("Timed out"));
        cat3.ShouldBe(FailureCategory.Timeout);

        var (cat4, _) = connector.MapFailure(new OperationCanceledException());
        cat4.ShouldBe(FailureCategory.ConnectionDown);

        var (cat5, _) = connector.MapFailure(new InvalidOperationException("Something bad"));
        cat5.ShouldBe(FailureCategory.InternalError);
    }
}
