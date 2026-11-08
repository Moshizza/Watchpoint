using System.Threading.Channels;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Watchpoint.Host;
using Xunit;

namespace Watchpoint.Hosting.Tests;

[Trait("layer", "U")]
[Trait("spec", "HST")]
public sealed class SystemdWatchdogTests
{
    private const int Pid = 4242;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HST_U_026_Watchdog_pings_every_half_interval()
    {
        var time = new FakeTimeProvider();
        var notifier = new FakeNotifier();
        var env = Env(("NOTIFY_SOCKET", "/run/notify"), ("WATCHDOG_USEC", "60000000"), ("WATCHDOG_PID", Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var watchdog = new SystemdWatchdog(notifier, NullLogger<SystemdWatchdog>.Instance, time, env, Pid);

        watchdog.GetInterval().ShouldBe(TimeSpan.FromSeconds(30));

        await watchdog.StartAsync(Ct);

        // First ping right away: systemd arms the watchdog when the process starts.
        (await notifier.NextAsync()).ShouldBe("WATCHDOG=1");

        time.Advance(TimeSpan.FromSeconds(29));
        notifier.TryNext().ShouldBeNull();

        time.Advance(TimeSpan.FromSeconds(1));
        (await notifier.NextAsync()).ShouldBe("WATCHDOG=1");

        time.Advance(TimeSpan.FromSeconds(30));
        (await notifier.NextAsync()).ShouldBe("WATCHDOG=1");

        await watchdog.StopAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(60));
        notifier.TryNext().ShouldBeNull();
        notifier.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(null, "60000000", null)]
    [InlineData("/run/notify", null, null)]
    [InlineData("/run/notify", "0", null)]
    [InlineData("/run/notify", "abc", null)]
    [InlineData("/run/notify", "60000000", "1")]
    [InlineData("/run/notify", "60000000", "not-a-pid")]
    public async Task HST_U_027_Watchdog_disabled_without_settings_or_other_pid(string? socket, string? usec, string? pid)
    {
        var time = new FakeTimeProvider();
        var notifier = new FakeNotifier();
        var env = Env(("NOTIFY_SOCKET", socket), ("WATCHDOG_USEC", usec), ("WATCHDOG_PID", pid));
        using var watchdog = new SystemdWatchdog(notifier, NullLogger<SystemdWatchdog>.Instance, time, env, Pid);

        watchdog.GetInterval().ShouldBeNull();

        await watchdog.StartAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await watchdog.StopAsync(Ct);

        notifier.Count.ShouldBe(0);
    }

    [Fact]
    public void HST_U_027_Watchdog_enabled_without_WATCHDOG_PID()
    {
        var env = Env(("NOTIFY_SOCKET", "/run/notify"), ("WATCHDOG_USEC", "2000000"));
        using var watchdog = new SystemdWatchdog(new FakeNotifier(), NullLogger<SystemdWatchdog>.Instance, new FakeTimeProvider(), env, Pid);

        watchdog.GetInterval().ShouldBe(TimeSpan.FromSeconds(1));
    }

    private static Func<string, string?> Env(params (string Name, string? Value)[] values)
    {
        var map = values.Where(v => v.Value is not null).ToDictionary(v => v.Name, v => v.Value);
        return name => map.GetValueOrDefault(name);
    }

    private sealed class FakeNotifier : ISystemdNotifier
    {
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private int _count;

        public bool IsEnabled => true;

        public int Count => Volatile.Read(ref _count);

        public void Notify(ServiceState state)
        {
            Interlocked.Increment(ref _count);
            _sent.Writer.TryWrite(state.ToString());
        }

        public async Task<string> NextAsync()
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            return await _sent.Reader.ReadAsync(cts.Token);
        }

        public string? TryNext() => _sent.Reader.TryRead(out var value) ? value : null;
    }
}
