using System.Text.Json;
using System.Xml;

namespace Watchpoint.Integration.Simulator.Engine.Scenario;

public sealed record ScenarioStep(
    string At,
    string? Device,
    string? Event,
    IReadOnlyDictionary<string, object?>? Set,
    IReadOnlyDictionary<string, object?>? Payload)
{
    public TimeSpan ParsedAt => XmlConvert.ToTimeSpan(At);
}

public sealed record ScenarioDefinition(
    bool Loop,
    bool WithRandom,
    IReadOnlyList<ScenarioStep> Steps);

public sealed class ScenarioPlayer
{
    private readonly ScenarioDefinition _scenario;
    private readonly TimeSpan _totalDuration;
    private TimeSpan _elapsed;
    private int _stepIndex;

    public bool WithRandom => _scenario.WithRandom;

    public ScenarioPlayer(ScenarioDefinition scenario)
    {
        _scenario = scenario;
        var maxAt = TimeSpan.Zero;
        foreach (var s in scenario.Steps)
        {
            var at = s.ParsedAt;
            if (at > maxAt)
            {
                maxAt = at;
            }
        }
        _totalDuration = maxAt;
    }

    public static ScenarioPlayer? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool loop = root.TryGetProperty("loop", out var l) && l.GetBoolean();
            bool withRandom = root.TryGetProperty("withRandom", out var wr) && wr.GetBoolean();
            var steps = new List<ScenarioStep>();
            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in stepsEl.EnumerateArray())
                {
                    string at = item.GetProperty("at").GetString()!;
                    string? device = item.TryGetProperty("device", out var d) ? d.GetString() : null;
                    string? ev = item.TryGetProperty("event", out var e) ? e.GetString() : null;
                    Dictionary<string, object?>? setDict = null;
                    if (item.TryGetProperty("set", out var s) && s.ValueKind == JsonValueKind.Object)
                    {
                        setDict = new Dictionary<string, object?>();
                        foreach (var prop in s.EnumerateObject())
                        {
                            setDict[prop.Name] = ExtractJsonValue(prop.Value);
                        }
                    }
                    Dictionary<string, object?>? payloadDict = null;
                    if (item.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        payloadDict = new Dictionary<string, object?>();
                        foreach (var prop in p.EnumerateObject())
                        {
                            payloadDict[prop.Name] = ExtractJsonValue(prop.Value);
                        }
                    }

                    steps.Add(new ScenarioStep(at, device, ev, setDict, payloadDict));
                }
            }

            return new ScenarioPlayer(new ScenarioDefinition(loop, withRandom, steps));
        }
        catch
        {
            return null;
        }
    }

    private static object? ExtractJsonValue(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.TryGetInt64(out var i) ? (object)i : elem.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => elem.GetRawText(),
        };
    }

    public List<ScenarioStep> Advance(TimeSpan delta)
    {
        var executed = new List<ScenarioStep>();
        if (_scenario.Steps.Count == 0)
        {
            return executed;
        }

        _elapsed += delta;

        while (_stepIndex < _scenario.Steps.Count)
        {
            var next = _scenario.Steps[_stepIndex];
            if (_elapsed >= next.ParsedAt)
            {
                executed.Add(next);
                _stepIndex++;
            }
            else
            {
                break;
            }
        }

        if (_scenario.Loop && _stepIndex >= _scenario.Steps.Count && _elapsed >= _totalDuration)
        {
            _stepIndex = 0;
            _elapsed = TimeSpan.Zero;
        }

        return executed;
    }
}
