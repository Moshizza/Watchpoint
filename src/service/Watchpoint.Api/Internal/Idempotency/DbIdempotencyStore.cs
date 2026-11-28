using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Watchpoint.Api.Internal.Idempotency;

/// <summary>Idempotency records in <c>wp_api.idempotency</c> (spec API §5.5), shared by the nodes of a site.</summary>
internal sealed class DbIdempotencyStore(IDbContextFactory<IdempotencyDbContext> contexts, TimeProvider time) : IIdempotencyStore
{
    public async ValueTask<IdempotencyBegin> BeginAsync(IdempotencyRequest request, CancellationToken ct)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.Idempotency.Add(new IdempotencyRow
            {
                UserId = request.User.Value,
                Key = request.Key,
                Method = request.Method,
                Route = request.Route,
                BodySha256 = request.BodySha256,
                Status = 0,
                CreatedAt = time.GetUtcNow(),
            });
            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return new IdempotencyBegin(IdempotencyBeginKind.Started);
            }
            catch (DbUpdateException)
            {
                // The key exists (primary key): compare with the stored request below.
            }
        }

        await using var read = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await read.Idempotency.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == request.User.Value && r.Key == request.Key, ct).ConfigureAwait(false);
        if (row is null)
        {
            return await BeginAsync(request, ct).ConfigureAwait(false);
        }

        if (row.Method != request.Method || row.Route != request.Route || row.BodySha256 != request.BodySha256)
        {
            return new IdempotencyBegin(IdempotencyBeginKind.Mismatch);
        }

        if (row.Status == 0)
        {
            return new IdempotencyBegin(IdempotencyBeginKind.InProgress);
        }

        var headers = row.ResponseHeaders is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(row.ResponseHeaders) ?? [];
        return new IdempotencyBegin(IdempotencyBeginKind.Replay, new StoredResponse(row.Status, row.ResponseBody ?? [], headers, row.CreatedAt));
    }

    public async ValueTask CompleteAsync(IdempotencyRequest request, StoredResponse response, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var headers = JsonSerializer.Serialize(response.Headers);
        await db.Idempotency.Where(r => r.UserId == request.User.Value && r.Key == request.Key)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, response.Status).SetProperty(r => r.ResponseBody, response.Body)
                .SetProperty(r => r.ResponseHeaders, headers).SetProperty(r => r.CreatedAt, response.CreatedAt), ct).ConfigureAwait(false);
    }

    public async ValueTask AbandonAsync(IdempotencyRequest request, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Idempotency.Where(r => r.UserId == request.User.Value && r.Key == request.Key && r.Status == 0).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<int> PurgeAsync(DateTimeOffset createdBefore, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Idempotency.Where(r => r.CreatedAt < createdBefore).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
