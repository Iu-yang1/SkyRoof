using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Serilog;
using VE3NEA.SkyTlm.Core;

namespace SkyRoof
{
  // KISS-over-TCP sends a complete escaped frame or disconnects a client;
  // dropping arbitrary bytes would corrupt the protocol stream.
  public sealed class KissServer : IDisposable
  {
    private const byte FEND = 0xC0;
    private const byte FESC = 0xDB;
    private const byte TFEND = 0xDC;
    private const byte TFESC = 0xDD;
    private const int QueueCapacity = 32;

    private sealed class ClientSession
    {
      public readonly TcpClient Socket;
      public readonly Channel<byte[]> Outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(QueueCapacity)
        {
          SingleReader = true,
          SingleWriter = false,
          FullMode = BoundedChannelFullMode.Wait
        });

      public ClientSession(TcpClient socket) => Socket = socket;
    }

    private readonly object gate = new();
    private readonly List<ClientSession> clients = new();
    private readonly HashSet<Task> clientTasks = new();
    private TcpListener? listener;
    private CancellationTokenSource? shutdown;
    private Task? acceptTask;
    private volatile bool active;

    public bool Active => active;
    public int ClientCount { get { lock (gate) return clients.Count; } }

    // Port zero is useful for deterministic loopback integration tests.
    public int ListeningPort
    {
      get
      {
        lock (gate)
          return listener?.LocalEndpoint is IPEndPoint ep ? ep.Port : 0;
      }
    }

    public void Start(int port)
    {
      Stop();
      TcpListener? next = null;
      CancellationTokenSource? cts = null;
      try
      {
        next = new TcpListener(IPAddress.Any, port);
        next.Start();
        cts = new CancellationTokenSource();
        lock (gate)
        {
          listener = next;
          shutdown = cts;
          active = true;
          acceptTask = AcceptClientsAsync(next, cts.Token);
        }
        Log.Information("KISS server listening on port {Port}", ListeningPort);
      }
      catch (Exception ex)
      {
        Log.Error(ex, "Failed to start KISS server on port {Port}", port);
        next?.Stop();
        cts?.Dispose();
        lock (gate)
        {
          if (ReferenceEquals(listener, next))
          {
            listener = null;
            shutdown = null;
            acceptTask = null;
            active = false;
          }
        }
      }
    }

    // Synchronous Stop is retained for existing UI callers. Closing sockets
    // and cancelling tokens makes all background operations finish promptly.
    public void Stop()
    {
      TcpListener? oldListener;
      CancellationTokenSource? cts;
      ClientSession[] snapshot;
      lock (gate)
      {
        active = false;
        oldListener = listener;
        listener = null;
        cts = shutdown;
        shutdown = null;
        snapshot = clients.ToArray();
        clients.Clear();
      }
      cts?.Cancel();
      oldListener?.Stop();
      foreach (ClientSession session in snapshot)
        CloseSession(session);
      cts?.Dispose();
    }

    // Test and hosting callers can await termination to detect leaked tasks.
    public async Task StopAsync()
    {
      Task[] pending;
      lock (gate)
      {
        pending = clientTasks.ToArray();
        if (acceptTask is { } accept)
          pending = pending.Append(accept).ToArray();
      }
      Stop();
      await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public void Dispose() => Stop();

    private async Task AcceptClientsAsync(TcpListener source, CancellationToken stop)
    {
      try
      {
        while (!stop.IsCancellationRequested)
        {
          TcpClient socket = await source.AcceptTcpClientAsync(stop).ConfigureAwait(false);
          socket.NoDelay = true;
          var session = new ClientSession(socket);
          lock (gate)
          {
            if (!active || !ReferenceEquals(listener, source))
            {
              CloseSession(session);
              continue;
            }
            clients.Add(session);
            Task task = ServeClientAsync(session, stop);
            clientTasks.Add(task);
            _ = task.ContinueWith(done =>
            {
              lock (gate) clientTasks.Remove(done);
              if (done.IsFaulted)
                Log.Error(done.Exception, "Unhandled KISS client worker failure");
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
              TaskScheduler.Default);
          }
        }
      }
      catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
      catch (SocketException) when (stop.IsCancellationRequested) { }
      catch (Exception ex)
      {
        Log.Error(ex, "KISS listener failed unexpectedly");
        // Do not leave a stopped accept loop reporting an active server.
        lock (gate)
          if (!ReferenceEquals(listener, source)) return;
        Stop();
      }
    }

    private async Task ServeClientAsync(ClientSession session, CancellationToken stop)
    {
      Task read = ReadClientAsync(session, stop);
      Task write = WriteClientAsync(session, stop);
      try
      {
        await Task.WhenAny(read, write).ConfigureAwait(false);
      }
      finally
      {
        RemoveClient(session);
        await Task.WhenAll(read, write).ConfigureAwait(false);
      }
    }

    // Client commands are accepted but intentionally ignored: this is a
    // receive-only telemetry KISS output, not a transmitting TNC.
    private static async Task ReadClientAsync(ClientSession session, CancellationToken stop)
    {
      try
      {
        byte[] buffer = new byte[256];
        NetworkStream stream = session.Socket.GetStream();
        while (await stream.ReadAsync(buffer, stop).ConfigureAwait(false) > 0) { }
      }
      catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      catch (ObjectDisposedException) { Log.Debug("KISS client input closed during shutdown"); }
      catch (IOException ex) { Log.Debug(ex, "KISS client input disconnected"); }
      catch (SocketException ex) { Log.Debug(ex, "KISS client input socket closed"); }
      catch (Exception ex) { Log.Warning(ex, "KISS client reader failed"); }
    }

    private static async Task WriteClientAsync(ClientSession session, CancellationToken stop)
    {
      try
      {
        NetworkStream stream = session.Socket.GetStream();
        await foreach (byte[] frame in session.Outbound.Reader.ReadAllAsync(stop)
          .ConfigureAwait(false))
          await stream.WriteAsync(frame, stop).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
      catch (ChannelClosedException) { Log.Debug("KISS output channel closed during shutdown"); }
      catch (ObjectDisposedException) { Log.Debug("KISS output socket closed during shutdown"); }
      catch (IOException ex) { Log.Debug(ex, "KISS client output disconnected"); }
      catch (SocketException ex) { Log.Debug(ex, "KISS client output socket closed"); }
      catch (Exception ex) { Log.Warning(ex, "KISS client writer failed"); }
    }

    private void RemoveClient(ClientSession session)
    {
      lock (gate) clients.Remove(session);
      CloseSession(session);
    }

    private static void CloseSession(ClientSession session)
    {
      session.Outbound.Writer.TryComplete();
      session.Socket.Dispose();
    }

    // Non-blocking for the producer/DSP thread: slow clients are disconnected
    // when their complete-frame queue reaches capacity.
    public void SendToAll(Frame frame)
    {
      if (!Active) return;
      byte[] payload = Encode(frame.Bytes);
      ClientSession[] snapshot;
      lock (gate) snapshot = clients.ToArray();
      foreach (ClientSession session in snapshot)
        if (!session.Outbound.Writer.TryWrite(payload))
        {
          Log.Warning("Dropping stalled KISS client (outbound queue full or closed)");
          RemoveClient(session);
        }
    }

    // KISS data frame, channel zero, without FCS.
    private static byte[] Encode(byte[] data)
    {
      var result = new List<byte>(data.Length + 4) { FEND, 0x00 };
      foreach (byte value in data)
      {
        if (value == FEND)
        { result.Add(FESC); result.Add(TFEND); }
        else if (value == FESC)
        { result.Add(FESC); result.Add(TFESC); }
        else result.Add(value);
      }
      result.Add(FEND);
      return result.ToArray();
    }
  }
}
