using System.Globalization;
using System.Text;

namespace Watchpoint.Spikes.NatsJetStream;

/// <summary>Collects measurement lines (Markdown) and prints them as they arrive.</summary>
internal sealed class Report
{
    private readonly StringBuilder _text = new();

    public void Heading(string title) => Line("\n### " + title + "\n");

    public void Row(string metric, string value) => Line("| " + metric + " | " + value + " |");

    public void TableHeader() => Line("| Measurement | Result |\n|---|---|");

    public void Line(string text)
    {
        Console.WriteLine(text);
        _text.Append(text).Append('\n');
    }

    public Task SaveAsync(string path) => File.WriteAllTextAsync(path, _text.ToString());

    public static string Rate(long count, TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{count / elapsed.TotalSeconds:N0} rec/s ({count:N0} in {elapsed.TotalSeconds:N2} s)");

    public static string Ms(double ms) => string.Create(CultureInfo.InvariantCulture, $"{ms:N2} ms");

    public static string Latency(List<double> samplesMs)
    {
        if (samplesMs.Count == 0)
        {
            return "no samples";
        }

        samplesMs.Sort();
        return string.Create(CultureInfo.InvariantCulture,
            $"p50 {Pct(samplesMs, 0.50):N2} ms, p95 {Pct(samplesMs, 0.95):N2} ms, p99 {Pct(samplesMs, 0.99):N2} ms, max {samplesMs[^1]:N2} ms (n={samplesMs.Count})");
    }

    private static double Pct(List<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
