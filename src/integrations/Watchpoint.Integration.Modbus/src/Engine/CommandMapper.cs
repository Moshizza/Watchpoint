using System.Globalization;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Modbus.Mapping;
using Watchpoint.Integration.Modbus.Protocol;
using Watchpoint.Integration.Modbus.Transport;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Modbus.Engine;

/// <summary>Translates platform commands to Modbus writes, handles pulses and verification.</summary>
public static class CommandMapper
{
    /// <summary>Executes a mapped command against the transport.</summary>
    public static async Task ExecuteCommandAsync(
        ITransport transport,
        byte unitId,
        CommandDefinition commandDef,
        ConnectorCommand command,
        Func<ModbusTable, ushort, ushort, Task<ReadOnlyMemory<byte>>> readBack,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var time = timeProvider ?? TimeProvider.System;

        foreach (var write in commandDef.Write)
        {
            await ExecuteWriteActionAsync(transport, unitId, write, command.Arguments, cancellationToken).ConfigureAwait(false);
        }

        if (commandDef.Pulse is not null)
        {
            var delay = TimeSpan.FromMilliseconds(commandDef.Pulse.ThenAfterMs);
            await Task.Delay(delay, time, cancellationToken).ConfigureAwait(false);

            foreach (var pulseWrite in commandDef.Pulse.ThenWrite)
            {
                await ExecuteWriteActionAsync(transport, unitId, pulseWrite, command.Arguments, cancellationToken).ConfigureAwait(false);
            }
        }

        if (commandDef.Verify is not null)
        {
            var verify = commandDef.Verify;
            var (vTable, vAddr) = TemplateLoader.ResolveAddress(verify.Table, verify.Address);
            var expectedVal = verify.Expected;

            var deadline = time.GetUtcNow().AddMilliseconds(verify.TimeoutMs);
            var verified = false;

            while (time.GetUtcNow() < deadline)
            {
                var resp = await readBack(vTable, vAddr, 1).ConfigureAwait(false);
                bool actualBool;
                if (vTable == ModbusTable.Coil || vTable == ModbusTable.DiscreteInput)
                {
                    actualBool = resp.Length > 0 && (resp.Span[0] & 1) != 0;
                }
                else
                {
                    actualBool = resp.Length >= 2 && resp.Span[1] != 0;
                }

                if (expectedVal is bool b && b == actualBool)
                {
                    verified = true;
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), time, cancellationToken).ConfigureAwait(false);
            }

            if (!verified)
            {
                throw new InvalidOperationException($"Command verification failed at address {vAddr}. Expected {expectedVal}.");
            }
        }
    }

    private static async Task ExecuteWriteActionAsync(
        ITransport transport,
        byte unitId,
        WriteActionDefinition write,
        IReadOnlyDictionary<string, WpValue> args,
        CancellationToken cancellationToken)
    {
        var (table, addr) = TemplateLoader.ResolveAddress(write.Table, write.Address);
        var rawVal = write.Value;

        if (rawVal is string strVal && args is not null)
        {
            foreach (var (argKey, argWpVal) in args)
            {
                if (strVal.Equals($"{{{argKey}}}", StringComparison.OrdinalIgnoreCase))
                {
                    rawVal = argWpVal.Kind switch
                    {
                        WpValueKind.Bool => argWpVal.AsBool(),
                        WpValueKind.Int => argWpVal.AsInt(),
                        WpValueKind.Decimal => argWpVal.AsDecimal(),
                        WpValueKind.String => argWpVal.AsString(),
                        _ => argWpVal.ToString()
                    };
                    break;
                }
            }
        }

        if (rawVal is System.Text.Json.JsonElement je)
        {
            if (table == ModbusTable.Coil)
            {
                var bVal = je.ValueKind == System.Text.Json.JsonValueKind.True;
                var pdu = ModbusPdu.BuildWriteSingleCoil(addr, bVal);
                await transport.SendAsync(unitId, pdu, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var regVal = (ushort)je.GetInt32();
                var pdu = ModbusPdu.BuildWriteSingleRegister(addr, regVal);
                await transport.SendAsync(unitId, pdu, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (table == ModbusTable.Coil)
        {
            var bVal = Convert.ToBoolean(rawVal, CultureInfo.InvariantCulture);
            var pdu = ModbusPdu.BuildWriteSingleCoil(addr, bVal);
            await transport.SendAsync(unitId, pdu, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var regVal = Convert.ToUInt16(rawVal, CultureInfo.InvariantCulture);
            var pdu = ModbusPdu.BuildWriteSingleRegister(addr, regVal);
            await transport.SendAsync(unitId, pdu, cancellationToken).ConfigureAwait(false);
        }
    }
}
