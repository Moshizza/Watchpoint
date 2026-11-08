using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Sdk.Logging;

/// <summary>
/// Extension methods for redacting protocol trace logging (INT §10).
/// Only logs when protocol trace is enabled, and masks declared secret fields.
/// </summary>
public static partial class RedactionExtensions
{
    private static readonly Regex DefaultSecretRegex = new(
        @"(?i)(""(?:password|secret|token|apikey|authorization|bearer|cookie)""\s*:\s*"")[^""]*("")|((?:password|secret|token|apikey|authorization)\s*=\s*)[^\s&;]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Masks declared sensitive field values within a protocol message payload.
    /// </summary>
    /// <param name="message">The raw message payload (e.g. JSON or key-value string).</param>
    /// <param name="secretFields">Additional secret field names to mask.</param>
    /// <returns>The payload with secrets masked as ***.</returns>
    public static string MaskSecrets(string message, IEnumerable<string>? secretFields = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }

        var masked = DefaultSecretRegex.Replace(message, match =>
        {
            if (match.Groups[1].Success)
            {
                return match.Groups[1].Value + "***" + match.Groups[2].Value;
            }
            if (match.Groups[3].Success)
            {
                return match.Groups[3].Value + "***";
            }
            return match.Value;
        });

        if (secretFields is not null)
        {
            foreach (var field in secretFields)
            {
                if (string.IsNullOrWhiteSpace(field))
                {
                    continue;
                }

                var customPattern = $@"(?i)(""{Regex.Escape(field)}""\s*:\s*"")[^""]*("")|({Regex.Escape(field)}\s*=\s*)[^\s&;]+";
                masked = Regex.Replace(masked, customPattern, match =>
                {
                    if (match.Groups[1].Success)
                    {
                        return match.Groups[1].Value + "***" + match.Groups[2].Value;
                    }
                    if (match.Groups[3].Success)
                    {
                        return match.Groups[3].Value + "***";
                    }
                    return match.Value;
                });
            }
        }

        return masked;
    }

    /// <summary>
    /// Logs a protocol trace message if protocol trace is enabled, masking any secret fields.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="traceEnabled">Whether protocol trace is enabled for the connection.</param>
    /// <param name="direction">Direction (e.g. "SENT", "RECEIVED").</param>
    /// <param name="payload">Payload content.</param>
    /// <param name="secretFields">Declared secret fields to redact.</param>
    public static void LogProtocolTrace(
        this ILogger logger,
        bool traceEnabled,
        string direction,
        string payload,
        IEnumerable<string>? secretFields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!traceEnabled)
        {
            return;
        }

        var masked = MaskSecrets(payload, secretFields);
        SdkLog.ProtocolTrace(logger, direction, masked);
    }
}
