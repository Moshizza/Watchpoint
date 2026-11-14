using System.IO.Hashing;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// Maps an entity to its log partition (spec PIP §4). The function is part of the stored data format: it must never
/// change, on any platform or version. Definition: the 16 bytes of the UUID in RFC 9562 network order (the order of
/// the hexadecimal digits in its text form), hashed with XXH32 (seed 0), modulo <see cref="PartitionCount"/>.
/// </summary>
public static class Partitioning
{
    /// <summary>The number of partitions. Fixed forever: changing it is a data migration.</summary>
    public const int PartitionCount = 64;

    /// <summary>Returns the partition of an entity id.</summary>
    /// <param name="entityId">The entity id (device or server).</param>
    /// <returns>The partition, 0 to 63.</returns>
    public static int Of(Guid entityId)
    {
        Span<byte> bytes = stackalloc byte[16];
        entityId.TryWriteBytes(bytes, bigEndian: true, out _);
        return (int)(XxHash32.HashToUInt32(bytes, seed: 0) % PartitionCount);
    }

    /// <summary>Returns the partition of a device.</summary>
    /// <param name="id">The device id.</param>
    /// <returns>The partition, 0 to 63.</returns>
    public static int Of(DeviceId id) => Of(id.Value);

    /// <summary>Returns the partition of a server (servers are partitioned like devices).</summary>
    /// <param name="id">The server id.</param>
    /// <returns>The partition, 0 to 63.</returns>
    public static int Of(ServerId id) => Of(id.Value);

    /// <summary>Returns whether <paramref name="partition"/> is a valid partition number.</summary>
    /// <param name="partition">The partition.</param>
    /// <returns><see langword="true"/> for 0 to 63.</returns>
    public static bool IsValid(int partition) => (uint)partition < PartitionCount;
}
