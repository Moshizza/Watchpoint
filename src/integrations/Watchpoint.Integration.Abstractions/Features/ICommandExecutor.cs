using Watchpoint.Integration.Abstractions.Model;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Executes commands (CMD dispatch) (INT §6). Never called for commands the device type does not declare.</summary>
public interface ICommandExecutor
{
    /// <summary>Executes one command.</summary>
    /// <param name="command">The command.</param>
    /// <param name="ct">Cancelled at the command's deadline.</param>
    /// <returns>The result; <see cref="ConnectorCommandResult.NotSupported"/> when the device lacks the command.</returns>
    ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectorCommand command, CancellationToken ct);
}

/// <summary>Executes batchable commands of a group command in one request (INT §6).</summary>
public interface IBatchCommandExecutor
{
    /// <summary>Executes the commands.</summary>
    /// <param name="commands">The commands.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>One result per command, in the same order.</returns>
    ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(IReadOnlyList<ConnectorCommand> commands, CancellationToken ct);
}
