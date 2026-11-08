using Microsoft.EntityFrameworkCore;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Storage.Internal;

internal sealed class EfModuleMigrator(ModuleContextActivator activator) : IModuleMigrator
{
    public async Task<IReadOnlyList<string>> MigrateAsync(IModuleStorageRegistration registration, CancellationToken ct)
    {
        await using var context = activator.Create(registration);
        var pending = (await context.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        try
        {
            await context.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            throw new ModuleMigrationException(pending.FirstOrDefault(m => !applied.Contains(m)) ?? string.Empty, ex);
        }

        return pending;
    }
}
