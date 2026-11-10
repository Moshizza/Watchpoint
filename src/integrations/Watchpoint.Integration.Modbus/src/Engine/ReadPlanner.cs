using Watchpoint.Integration.Modbus.Mapping;

namespace Watchpoint.Integration.Modbus.Engine;

/// <summary>Represents a contiguous block of Modbus registers or coils to read in one request.</summary>
public sealed record ReadBlock(
    ModbusTable Table,
    ushort StartingAddress,
    ushort Quantity,
    IReadOnlyList<PointDefinition> Points);

/// <summary>Optimizes and batches Modbus read requests.</summary>
public static class ReadPlanner
{
    /// <summary>Plans and coalesces individual point reads into contiguous blocks.</summary>
    public static IReadOnlyList<ReadBlock> PlanReads(
        IEnumerable<PointDefinition> points,
        int maxGap = 10,
        int maxRegisters = 120,
        int maxCoils = 2000)
    {
        var blocks = new List<ReadBlock>();

        var groupedByTable = points.GroupBy(p => p.Table);

        foreach (var group in groupedByTable)
        {
            var table = group.Key;
            var isCoilLike = table == ModbusTable.Coil || table == ModbusTable.DiscreteInput;
            var maxBlockSize = isCoilLike ? maxCoils : maxRegisters;

            var sorted = group.OrderBy(p => p.Address).ToList();
            if (sorted.Count == 0) continue;

            var currentPoints = new List<PointDefinition> { sorted[0] };
            var blockStart = sorted[0].Address;
            var blockEnd = blockStart + (sorted[0].Table == ModbusTable.Coil || sorted[0].Table == ModbusTable.DiscreteInput ? 1 : sorted[0].RegisterCount);

            for (var i = 1; i < sorted.Count; i++)
            {
                var pt = sorted[i];
                var ptLength = isCoilLike ? 1 : pt.RegisterCount;
                var ptEnd = pt.Address + ptLength;

                var gap = pt.Address - blockEnd;
                var potentialEnd = Math.Max(blockEnd, ptEnd);
                var potentialSpan = potentialEnd - blockStart;

                if (gap <= maxGap && potentialSpan <= maxBlockSize)
                {
                    currentPoints.Add(pt);
                    blockEnd = (ushort)potentialEnd;
                }
                else
                {
                    blocks.Add(new ReadBlock(table, (ushort)blockStart, (ushort)(blockEnd - blockStart), currentPoints));
                    currentPoints = [pt];
                    blockStart = pt.Address;
                    blockEnd = blockStart + ptLength;
                }
            }

            if (currentPoints.Count > 0)
            {
                blocks.Add(new ReadBlock(table, (ushort)blockStart, (ushort)(blockEnd - blockStart), currentPoints));
            }
        }

        return blocks;
    }
}
