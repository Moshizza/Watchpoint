using System.Text;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Fake implementation of <see cref="ISecretAccessor"/>.
/// </summary>
public sealed class TestSecretAccessor : ISecretAccessor
{
    private readonly Dictionary<string, string> _secrets;

    /// <summary>Initializes a new instance of <see cref="TestSecretAccessor"/> with given secrets.</summary>
    public TestSecretAccessor(IReadOnlyDictionary<string, string>? secrets = null)
    {
        _secrets = secrets != null ? new Dictionary<string, string>(secrets, StringComparer.Ordinal) : [];
    }

    /// <inheritdoc />
    public ValueTask<ConnectorSecret?> GetAsync(string fieldId, CancellationToken ct)
    {
        if (_secrets.TryGetValue(fieldId, out var val))
        {
            var bytes = Encoding.UTF8.GetBytes(val);
            return ValueTask.FromResult<ConnectorSecret?>(new ConnectorSecret(bytes));
        }

        return ValueTask.FromResult<ConnectorSecret?>(null);
    }
}
