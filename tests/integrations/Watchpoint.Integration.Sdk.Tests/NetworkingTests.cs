using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Shouldly;
using Watchpoint.Integration.Sdk.Net;
using Xunit;

namespace Watchpoint.Integration.Sdk.Tests;

public sealed class NetworkingTests
{
    [Fact]
    public async Task INT_U_030_TcpRequestClient_connect_timeout()
    {
        // INT-U-030: unreachable address -> timeout <= 5.5 s
        // Using a non-routable IP (TEST-NET-1: 192.0.2.1) with connect timeout 2.0s
        var sw = Stopwatch.StartNew();
        var client = new TcpRequestClient(
            "192.0.2.1",
            12345,
            connectTimeout: TimeSpan.FromSeconds(2.0),
            time: TimeProvider.System);

        try
        {
            await Should.ThrowAsync<Exception>(async () =>
            {
                await client.SendRequestAsync<byte[]>(
                    "test"u8.ToArray(),
                    (reader, ct) => ValueTask.FromResult<byte[]>([]),
                    TestContext.Current.CancellationToken);
            });

            sw.Elapsed.TotalSeconds.ShouldBeLessThan(5.5);
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task INT_U_031_TcpRequestClient_read_timeout_and_cancellation()
    {
        // In-process TCP server that accepts connection but doesn't write response
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            using var stream = serverClient.GetStream();
            var buf = new byte[100];
            _ = await stream.ReadAsync(buf, TestContext.Current.CancellationToken);
            // Wait without responding until cancelled
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var client = new TcpRequestClient(
            IPAddress.Loopback.ToString(),
            port,
            connectTimeout: TimeSpan.FromSeconds(2),
            readTimeout: TimeSpan.FromMilliseconds(200),
            time: TimeProvider.System);

        try
        {
            // 1. Read timeout
            await Should.ThrowAsync<TimeoutException>(async () =>
            {
                await client.SendRequestAsync<byte[]>(
                    "ping"u8.ToArray(),
                    async (reader, ct) =>
                    {
                        var result = await reader.ReadAsync(ct);
                        return result.Buffer.ToArray();
                    },
                    TestContext.Current.CancellationToken);
            });

            // 2. Cancellation
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
            {
                await client.SendRequestAsync<byte[]>(
                    "ping"u8.ToArray(),
                    (reader, ct) => ValueTask.FromResult<byte[]>([]),
                    cts.Token);
            });
        }
        finally
        {
            await client.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task INT_U_032_FramedStream_length_prefix_and_delimiter_framing_max_frame_size()
    {
        // 1. Delimiter framing
        var pipe1 = new Pipe();
        await pipe1.Writer.WriteAsync("hello\nworld\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await pipe1.Writer.FlushAsync(TestContext.Current.CancellationToken);

        var frame1 = await FramedStream.ReadDelimiterFramedAsync(pipe1.Reader, "\n"u8.ToArray(), ct: TestContext.Current.CancellationToken);
        System.Text.Encoding.UTF8.GetString(frame1!).ShouldBe("hello");

        var frame2 = await FramedStream.ReadDelimiterFramedAsync(pipe1.Reader, "\n"u8.ToArray(), ct: TestContext.Current.CancellationToken);
        System.Text.Encoding.UTF8.GetString(frame2!).ShouldBe("world");

        // Max frame size check with delimiter framing
        var pipeMax = new Pipe();
        await pipeMax.Writer.WriteAsync("toolargepayload\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await pipeMax.Writer.FlushAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await FramedStream.ReadDelimiterFramedAsync(pipeMax.Reader, "\n"u8.ToArray(), maxFrameSize: 5, ct: TestContext.Current.CancellationToken);
        });

        // 2. Length-prefix framing (4-byte big endian)
        var pipe2 = new Pipe();
        var payloadBytes = "message payload"u8.ToArray();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payloadBytes.Length);

        await pipe2.Writer.WriteAsync(prefix, TestContext.Current.CancellationToken);
        await pipe2.Writer.WriteAsync(payloadBytes, TestContext.Current.CancellationToken);
        await pipe2.Writer.FlushAsync(TestContext.Current.CancellationToken);

        var frameLength = await FramedStream.ReadLengthPrefixedAsync(pipe2.Reader, ct: TestContext.Current.CancellationToken);
        System.Text.Encoding.UTF8.GetString(frameLength!).ShouldBe("message payload");

        // Length-prefix exceeding max frame size
        var pipeMaxLen = new Pipe();
        BinaryPrimitives.WriteInt32BigEndian(prefix, 5000);
        await pipeMaxLen.Writer.WriteAsync(prefix, TestContext.Current.CancellationToken);
        await pipeMaxLen.Writer.FlushAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await FramedStream.ReadLengthPrefixedAsync(pipeMaxLen.Reader, maxFrameSize: 100, ct: TestContext.Current.CancellationToken);
        });
    }
}
