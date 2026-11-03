namespace Watchpoint.Kernel.Results;

/// <summary>Exceptions for invalid accesses to results.</summary>
internal static class ResultGuard
{
    public const string Uninitialised = "uninitialised result";

    public static InvalidOperationException NoError(bool initialised) =>
        new(initialised ? "The result is a success and has no error." : Uninitialised);

    public static InvalidOperationException NoValue(bool initialised) =>
        new(initialised ? "The result is a failure and has no value." : Uninitialised);
}
