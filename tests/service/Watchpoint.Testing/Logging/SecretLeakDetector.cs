using System.Net;
using System.Text;

namespace Watchpoint.Testing.Logging;

/// <summary>
/// Fails a test when a registered secret appears in captured logs: in the rendered message, the template,
/// a property value or the exception text; plain, base64 or URL-encoded; compared case-insensitively.
/// </summary>
public sealed class SecretLeakDetector
{
    private readonly List<string> _secrets = [];

    /// <summary>Registers a secret value used by the test (for example a password).</summary>
    /// <param name="secretValue">The secret; must not be empty.</param>
    public void Register(string secretValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(secretValue);
        _secrets.Add(secretValue);
    }

    /// <summary>Asserts that no registered secret appears in <paramref name="logs"/>.</summary>
    /// <param name="logs">The captured logs.</param>
    /// <exception cref="TestAssertionException">A secret was found; the message names the entry and field, never the secret.</exception>
    public void AssertNoLeak(LogCollector logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var findings = new List<string>();
        var entries = logs.Entries;
        for (var s = 0; s < _secrets.Count; s++)
        {
            var forms = Forms(_secrets[s]);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                foreach (var (field, text) in Fields(entry))
                {
                    foreach (var (formName, form) in forms)
                    {
                        if (text.Contains(form, StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add($"secret #{s + 1} ({formName}) in entry #{i} [{entry.Category}, event {entry.EventId}] {field}");
                        }
                    }
                }
            }
        }

        if (findings.Count > 0)
        {
            throw new TestAssertionException("Secret leaked into logs:" + Environment.NewLine + string.Join(Environment.NewLine, findings.Distinct()));
        }
    }

    private static List<(string Name, string Form)> Forms(string secret)
    {
        var forms = new List<(string, string)> { ("plain", secret) };
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
        forms.Add(("base64", base64));
        var trimmed = base64.TrimEnd('=');
        if (trimmed != base64)
        {
            forms.Add(("base64", trimmed));
        }

        foreach (var url in new[] { Uri.EscapeDataString(secret), WebUtility.UrlEncode(secret) })
        {
            if (url != secret)
            {
                forms.Add(("url-encoded", url));
            }
        }

        return forms;
    }

    private static IEnumerable<(string Field, string Text)> Fields(CapturedLog entry)
    {
        yield return ("message", entry.RenderedMessage);
        yield return ("template", entry.MessageTemplate);
        foreach (var (key, value) in entry.Properties)
        {
            if (value?.ToString() is { } text)
            {
                yield return ($"property '{key}'", text);
            }
        }

        if (entry.Exception is not null)
        {
            yield return ("exception", entry.Exception.ToString());
        }
    }
}
