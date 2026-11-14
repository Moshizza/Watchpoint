namespace Watchpoint.Hosting;

/// <summary>
/// Thrown by a module whose settings are invalid while the host composes the services (spec HST §4): the host logs
/// <c>HST1101</c> with <see cref="Problems"/> and exits with code 78 (<c>EX_CONFIG</c>) instead of starting with
/// defaults the operator did not choose.
/// </summary>
public sealed class InvalidSettingsException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="module">The module whose settings are invalid.</param>
    /// <param name="problems">The problems, one per setting (keys and codes, never values).</param>
    public InvalidSettingsException(string module, string problems)
        : base($"{module} settings are invalid: {problems}")
    {
        Module = module;
        Problems = problems;
    }

    /// <summary>Creates the exception without details.</summary>
    public InvalidSettingsException()
        : this("Unknown", "no details")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public InvalidSettingsException(string message)
        : base(message)
    {
        Module = "Unknown";
        Problems = message;
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public InvalidSettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
        Module = "Unknown";
        Problems = message;
    }

    /// <summary>Gets the module whose settings are invalid.</summary>
    public string Module { get; }

    /// <summary>Gets the problems.</summary>
    public string Problems { get; }
}
