using Watchpoint.Commands.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;

namespace Watchpoint.Commands.Internal.Verification;

/// <summary>The current value of a device property, as the state module has it (CMD §9: checked at success time).</summary>
internal interface ICommandStateView
{
    WpValue? Current(DeviceId device, PropertyPath property);
}

/// <summary>The state module's live store (STA applies a batch before the commands subscriber, order 100 &lt; 200).</summary>
internal sealed class StateReaderView(IStateReader state) : ICommandStateView
{
    public WpValue? Current(DeviceId device, PropertyPath property) =>
        state.Get(device.Value).TryGetValue(out var found, out _) && found.Properties.TryGetValue(property, out var value) ? value.Value : null;
}

/// <summary>Without a state module, no current value is known: effects are seen only through property records.</summary>
internal sealed class NoStateView : ICommandStateView
{
    public WpValue? Current(DeviceId device, PropertyPath property) => null;
}

/// <summary>
/// The verification watchers of the succeeded commands with an expected effect (spec CMD §9), per partition. Used only
/// on the worker of each partition. A watcher ends when its command is verified, unverified or leaves the store.
/// </summary>
internal sealed class Verifier
{
    /// <summary>The window when the expected effect has no <c>within</c>.</summary>
    public static readonly TimeSpan DefaultWithin = TimeSpan.FromSeconds(10);

    private readonly Dictionary<DeviceId, List<Watch>>[] _byDevice =
        [.. Enumerable.Range(0, Pipeline.Contracts.Partitioning.PartitionCount).Select(_ => new Dictionary<DeviceId, List<Watch>>())];

    /// <summary>The watchers of a partition.</summary>
    public int Count(int partition) => _byDevice[partition].Values.Sum(w => w.Count);

    /// <summary>Starts watching a succeeded command (deadline = success + <c>within</c>).</summary>
    public Watch? Register(int partition, Command command, DateTimeOffset succeededAt)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Verification is not { } effect)
        {
            return null;
        }

        Remove(partition, command.Id, command.Target);
        var watch = new Watch(command.Id, command.Target, effect, succeededAt, succeededAt + (effect.Within ?? DefaultWithin));
        var devices = _byDevice[partition];
        if (!devices.TryGetValue(command.Target, out var watches))
        {
            devices[command.Target] = watches = [];
        }

        watches.Add(watch);
        return watch;
    }

    public void Remove(int partition, CommandId id, DeviceId device)
    {
        var devices = _byDevice[partition];
        if (devices.TryGetValue(device, out var watches) && watches.RemoveAll(w => w.Id == id) > 0 && watches.Count == 0)
        {
            devices.Remove(device);
        }
    }

    public bool Watches(int partition, DeviceId device) => _byDevice[partition].ContainsKey(device);

    /// <summary>The watchers of <paramref name="device"/> that <paramref name="value"/> of <paramref name="property"/> satisfies; they are removed.</summary>
    public List<Watch> TakeMatching(int partition, DeviceId device, string property, WpValue? value)
    {
        var devices = _byDevice[partition];
        if (!devices.TryGetValue(device, out var watches))
        {
            return [];
        }

        var matched = watches.Where(w => w.Effect.Property.Value == property && Matches(w.Effect, value)).ToList();
        foreach (var watch in matched)
        {
            Remove(partition, watch.Id, device);
        }

        return matched;
    }

    /// <summary>The watchers past their deadline; they are removed.</summary>
    public List<Watch> TakeExpired(int partition, DateTimeOffset now)
    {
        var expired = _byDevice[partition].Values.SelectMany(w => w).Where(w => w.Deadline <= now).ToList();
        foreach (var watch in expired)
        {
            Remove(partition, watch.Id, watch.Device);
        }

        return expired;
    }

    /// <summary>Every watcher of a partition (a copy).</summary>
    public List<Watch> All(int partition) => [.. _byDevice[partition].Values.SelectMany(w => w)];

    /// <summary>Whether a value satisfies an expected effect: <c>equals</c> and/or <c>in</c>.</summary>
    public static bool Matches(ExpectedEffect effect, WpValue? value)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (value is not { } actual)
        {
            return false;
        }

        if (effect.EqualsValue is { } expected && !expected.Equals(actual))
        {
            return false;
        }

        if (effect.In is { } allowed && !allowed.Contains(actual))
        {
            return false;
        }

        return effect.EqualsValue is not null || effect.In is not null;
    }

    /// <param name="Id">The command.</param>
    /// <param name="Device">The target.</param>
    /// <param name="Effect">The expected effect.</param>
    /// <param name="SucceededAt">When the command succeeded.</param>
    /// <param name="Deadline">When it is unverified.</param>
    internal sealed record Watch(CommandId Id, DeviceId Device, ExpectedEffect Effect, DateTimeOffset SucceededAt, DateTimeOffset Deadline);
}
