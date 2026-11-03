using System.Text.Json.Serialization;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Kernel.Tests.Json;

public sealed record DateHolder(DateTimeOffset At);

public sealed record DurationHolder(TimeSpan Duration);

public sealed record KindHolder(ErrorKind Kind);

public sealed record NumberHolder(double Number);

public sealed record Settings(string Name, int Port);

public sealed record NameHolder(LocalizedName Name);


[JsonSerializable(typeof(DateHolder))]
[JsonSerializable(typeof(DurationHolder))]
[JsonSerializable(typeof(KindHolder))]
[JsonSerializable(typeof(NumberHolder))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(NameHolder))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
