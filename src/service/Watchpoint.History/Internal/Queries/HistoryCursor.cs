using System.Globalization;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Queries;

/// <summary>Keyset cursors of history pages: position (time, id), cursor version 1.</summary>
internal static class HistoryCursor
{
    private const int Version = 1;

    public static Result<HistoryKeyset?> Decode(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return Result.Success<HistoryKeyset?>(null);
        }

        if (!CursorCodec.Decode(cursor, Version).TryGetValue(out var decoded, out var error))
        {
            return error;
        }

        if (decoded.KeyValues.Count != 1
            || decoded.KeyValues[0].ValueKind != System.Text.Json.JsonValueKind.String
            || !DateTimeOffset.TryParse(decoded.KeyValues[0].GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            return CommonErrors.CursorInvalid();
        }

        return new HistoryKeyset(time, decoded.LastId);
    }

    public static string Encode(DateTimeOffset time, Guid id) => CursorCodec.Encode(Version, [time], id);

    /// <summary>Builds a page from up to <c>limit + 1</c> rows: the extra row only tells that more follow.</summary>
    public static Page<TOut> ToPage<TRow, TOut>(IReadOnlyList<TRow> rows, int limit, Func<TRow, DateTimeOffset> time, Func<TRow, Guid> id, Func<TRow, TOut> map)
    {
        var more = rows.Count > limit;
        var kept = more ? rows.Take(limit).ToList() : rows;
        var cursor = more ? HistoryCursor.Encode(time(kept[^1]), id(kept[^1])) : null;
        return new Page<TOut>([.. kept.Select(map)], cursor);
    }
}
