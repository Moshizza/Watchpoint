using System.Text.Json.Serialization;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Kernel.Tests.Json;

public sealed record DateHolder(DateTimeOffset At);

public sealed record DurationHolder(TimeSpan Duration);

public sealed record KindHolder(ErrorKind Kind);

public sealed record NumberHolder(double Number);

public sealed record Settings(string Name, int Port);

public sealed record NameHolder(LocalizedName Name);

public sealed record ValueHolder(WpValue Value);


[JsonSerializable(typeof(DateHolder))]
[JsonSerializable(typeof(DurationHolder))]
[JsonSerializable(typeof(KindHolder))]
[JsonSerializable(typeof(NumberHolder))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(NameHolder))]
[JsonSerializable(typeof(ValueHolder))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
