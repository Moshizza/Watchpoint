using Watchpoint.Integration.Modbus.Protocol;

namespace Watchpoint.Integration.Modbus.Transport;

/// <summary>Defines transport interaction for sending Modbus requests.</summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the transport is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Establishes the connection.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Closes the connection.</summary>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>Sends a Modbus PDU and awaits its response.</summary>
    Task<ModbusPdu> SendAsync(byte unitId, ModbusPdu request, CancellationToken cancellationToken);
}
