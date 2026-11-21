using System.Diagnostics.CodeAnalysis;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.State.Contracts;

/// <summary>Reads the live state of devices and servers (spec STA §5).</summary>
public interface IStateReader
{
    /// <summary>Gets the state of one entity.</summary>
    /// <param name="entityId">The device or server id.</param>
    /// <returns>The state, or <c>state.entity.not_found</c>.</returns>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Name fixed by spec STA §5.")]
    Result<DeviceState> Get(Guid entityId);

    /// <summary>Gets the state of several entities; ids with no state are omitted.</summary>
    /// <param name="entityIds">The device or server ids.</param>
    /// <returns>The states found, in no particular order.</returns>
    IReadOnlyList<DeviceState> GetMany(IReadOnlyCollection<Guid> entityIds);

    /// <summary>Queries devices by registry filters, overall state and a property condition.</summary>
    /// <param name="query">The query.</param>
    /// <param name="page">The page request.</param>
    /// <returns>A page of matching states, or <c>state.query.invalid_condition</c>.</returns>
    Result<Page<DeviceState>> Query(StateQuery query, PageRequest page);

    /// <summary>Gets the highest event log sequence applied across all owned partitions.</summary>
    long CurrentSequence { get; }
}
