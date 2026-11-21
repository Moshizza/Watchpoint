using System.Collections.Concurrent;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Policies;

/// <summary>
/// The last stored row of each (device, path) with a deadband policy (spec HIS §6), kept in memory and rebuilt lazily from the
/// latest row in the database the first time a key is seen. When the cache is full it is emptied (rebuilt lazily again).
/// </summary>
internal sealed class LastStoredCache(int capacity)
{
    private readonly ConcurrentDictionary<(Guid Device, string Path), HistoryPropertyRow> _rows = new();

    public int Count => _rows.Count;

    public bool TryGet(Guid device, string path, out HistoryPropertyRow row) => _rows.TryGetValue((device, path), out row!);

    public void Set(HistoryPropertyRow row)
    {
        if (_rows.Count >= capacity && !_rows.ContainsKey((row.DeviceId, row.Path)))
        {
            _rows.Clear();
        }

        _rows[(row.DeviceId, row.Path)] = row;
    }
}
