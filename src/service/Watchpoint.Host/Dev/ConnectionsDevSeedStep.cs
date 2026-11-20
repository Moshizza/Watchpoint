using System.Text.Json;
using Watchpoint.Hosting;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.Host.Dev;

/// <summary>
/// Seed section <c>connections</c> (HST §9): creates the connections (secrets go to the vault) through
/// <see cref="IConnectionManager"/>, enabled. The import follows at the <c>Connections</c> phase: on its first connect a
/// connection describes its inventory and the registry imports everything new when <c>autoImport</c> is set (IRT §7.4).
/// A connection whose name exists is kept.
/// </summary>
public sealed class ConnectionsDevSeedStep(IConnectionManager connections, IRegistryReader reader, NodeSettings settings) : IDevSeedStep
{
    public string Section => "connections";

    public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var names = connections.List().Select(c => c.Name.Default).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(seed.Connections.Any(c => names.Contains(c.Name)));
    }

    public async Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (settings.Node.Environment != NodeEnvironment.Development)
        {
            return Error.Forbidden(DevSeedReader.NotDevelopmentCode);
        }

        var existing = connections.List().Select(c => c.Name.Default).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < seed.Connections.Count; i++)
        {
            var connection = seed.Connections[i];
            if (existing.Contains(connection.Name))
            {
                continue;
            }

            var location = connection.Location is { } key ? DevSeed.FindLocation(seed, key, reader) : reader.GetLocationTree().FirstOrDefault(l => l.ParentId is null)?.Id;
            if (location is null)
            {
                return Error.Validation(DevSeedReader.InvalidCode, [CommonErrors.RequiredAt($"/connections/{i}/location")]);
            }

            if (!PackageId.Parse(connection.Package).TryGetValue(out var package, out var error))
            {
                return error;
            }

            using var document = JsonDocument.Parse(connection.Settings.ToJsonString());
            var created = await connections.CreateAsync(new NewConnection(LocalizedName.Of(connection.Name), package, null, connection.ServerType,
                document.RootElement.Clone(), connection.Secrets, location.Value, connection.AutoImport, StartNow: true), DevSeed.Context, ct).ConfigureAwait(false);
            if (!created.IsSuccess)
            {
                return created.Error;
            }
        }

        return Result.Success();
    }
}
