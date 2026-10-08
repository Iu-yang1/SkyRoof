using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class RotatorControlTests
{
    [Fact]
    public async Task PositionQueryReadsBothHamlibLines()
    {
        await using var server = new FakeRotctld((command, _) =>
            command == "p" ? "120.5\n45.75\n" : "RPRT 0\n");
        using var engine = StartEngine(server.Port);

        await WaitUntilAsync(() => engine.LastReadBearing != null);
        Assert.Equal(120.5, engine.LastReadBearing!.AzDeg, 2);
        Assert.Equal(45.75, engine.LastReadBearing.ElDeg, 2);
    }

    [Fact]
    public async Task RejectedPositionQueryDoesNotWaitForMissingElevation()
    {
        await using var server = new FakeRotctld((command, _) =>
            command == "p" ? "RPRT -11\n" : "RPRT 0\n");
        using var engine = StartEngine(server.Port);

        await WaitUntilAsync(() => server.Count("p") >= 2);
        Assert.Null(engine.LastReadBearing);
    }

    [Fact]
    public async Task FailedMoveIsRetriedWithoutChangingTarget()
    {
        int moves = 0;
        await using var server = new FakeRotctld((command, _) =>
        {
            if (command.StartsWith("P ", StringComparison.Ordinal))
                return Interlocked.Increment(ref moves) == 1
                    ? "RPRT -1\n"
                    : "RPRT 0\n";
            return "90.0\n30.0\n";
        });
        using var engine = StartEngine(server.Port);
        engine.RotateTo(new SkyRoof.Bearing(Math.PI / 2, Math.PI / 6));

        await WaitUntilAsync(() => Volatile.Read(ref moves) >= 2 &&
            engine.LastWrittenBearing != null);
        Assert.Equal(2, Volatile.Read(ref moves));
        Assert.Contains(server.Commands,
            command => command.StartsWith("P 90.0 30.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconnectResendsAcceptedMoveToRestartedController()
    {
        int moves = 0;
        int disconnects = 0;
        await using var server = new FakeRotctld((command, _) =>
        {
            if (command.StartsWith("P ", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref moves);
                return "RPRT 0\n";
            }

            // Simulate a controller reboot after acknowledging the first move:
            // close the first TCP session on the next position read.
            if (command == "p" && Interlocked.Increment(ref disconnects) == 1)
                return null;

            return "60.0\n10.0\n";
        });
        using var engine = StartEngine(server.Port);
        engine.RotateTo(new SkyRoof.Bearing(Math.PI / 3, Math.PI / 18));

        await WaitUntilAsync(() => Volatile.Read(ref moves) >= 2);
        Assert.True(server.ConnectionCount >= 2);
    }

    [Fact]
    public async Task FragmentedPositionReplyIsReassembled()
    {
        await using var server = new FakeRotctld((command, _) =>
            command == "p" ? "225.25\n12.75\n" : "RPRT 0\n",
            fragmentPositionReply: true);
        using var engine = StartEngine(server.Port);

        await WaitUntilAsync(() => engine.LastReadBearing != null);
        Assert.Equal(225.25, engine.LastReadBearing!.AzDeg, 2);
        Assert.Equal(12.75, engine.LastReadBearing.ElDeg, 2);
    }

    [Fact]
    public async Task StopCancelsTargetAndSendsOneStopCommand()
    {
        await using var server = new FakeRotctld((command, _) =>
            command == "p" ? "90.0\n30.0\n" : "RPRT 0\n");
        using var engine = StartEngine(server.Port);

        engine.RotateTo(new SkyRoof.Bearing(Math.PI / 2, Math.PI / 6));
        await WaitUntilAsync(() => server.Commands.Any(command =>
            command.StartsWith("P ", StringComparison.Ordinal)));
        engine.StopRotation();
        await WaitUntilAsync(() => server.Count("S") >= 1);
        await Task.Delay(150);

        Assert.Equal(1, server.Count("S"));
        Assert.Null(engine.RequestedBearing);
        Assert.Null(engine.LastWrittenBearing);
        Assert.Equal(1, server.Commands.Count(command =>
            command.StartsWith("P ", StringComparison.Ordinal)));
    }

    private static RotatorControlEngine StartEngine(ushort port)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            return new RotatorControlEngine(new RotatorSettings
            {
                Host = "127.0.0.1",
                Port = port,
                Delay = 25,
                SendTimeout = 500,
                ReceiveTimeout = 500,
                ReconnectDelay = 75
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.True(condition(), "Timed out waiting for the rotctld operation");
    }

    private sealed class FakeRotctld : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task worker;
        private readonly Func<string, int, string?> respond;
        private readonly bool fragmentPositionReply;
        private int commandNumber;
        private int connectionCount;

        public readonly ConcurrentQueue<string> Commands = new();
        public ushort Port { get; }
        public int ConnectionCount => Volatile.Read(ref connectionCount);

        public FakeRotctld(Func<string, int, string?> respond,
            bool fragmentPositionReply = false)
        {
            this.respond = respond;
            this.fragmentPositionReply = fragmentPositionReply;
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
            worker = Task.Run(ServeAsync);
        }

        public int Count(string command) => Commands.Count(item => item == command);

        private async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
                catch (SocketException) when (stop.IsCancellationRequested) { break; }

                Interlocked.Increment(ref connectionCount);
                using (client)
                {
                    try
                    {
                        using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII,
                            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                        while (!stop.IsCancellationRequested)
                        {
                            string? command = await reader.ReadLineAsync(stop.Token);
                            if (command == null) break;
                            Commands.Enqueue(command);
                            string? reply = respond(command, Interlocked.Increment(ref commandNumber));
                            if (reply == null) break; // drop the socket to simulate a reboot

                            if (fragmentPositionReply && command == "p")
                            {
                                int firstLineLength = reply.IndexOf('\n') + 1;
                                if (firstLineLength > 0 && firstLineLength < reply.Length)
                                {
                                    await stream.WriteAsync(
                                        Encoding.ASCII.GetBytes(reply[..firstLineLength]), stop.Token);
                                    await Task.Delay(30, stop.Token);
                                    await stream.WriteAsync(
                                        Encoding.ASCII.GetBytes(reply[firstLineLength..]), stop.Token);
                                    continue;
                                }
                            }
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), stop.Token);
                        }
                    }
                    catch (IOException) { } // client disconnected
                    catch (SocketException) { }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await worker; }
            catch (OperationCanceledException) { }
            stop.Dispose();
        }
    }
}
