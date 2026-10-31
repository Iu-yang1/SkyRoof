using System;
using System.IO;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  /// <summary>
  /// Real CAT worker connected to a synthetic rigctld/SkyCAT TCP server.
  /// No transceiver or audio output is involved. This tests actual command
  /// scheduling, STOP ordering and reconnection instead of a state-only mock.
  /// </summary>
  public sealed class SstvVirtualCatIntegrationTests
  {
    private static bool Until(Func<bool> condition, int timeoutMs = 7000) =>
      SpinWait.SpinUntil(condition, TimeSpan.FromMilliseconds(timeoutMs));

    [Fact]
    public void VirtualRadio_StopReturnsToReceiveAndDoesNotRekey()
    {
      using var rig = new VirtualRig { UseCrLf = true };
      using var cat = StartCat(rig);
      cat.SetPtt(true);
      Assert.True(Until(() => rig.IsTransmitting && cat.Ptt), "Virtual PTT ON was not received");
      Assert.True(Until(() => cat.LastPttOnHardwareReadbackUtc > DateTime.MinValue),
        "Missing actual virtual CAT PTT-ON readback");
      cat.ReleaseApplicationPtt();
      Assert.True(Until(() => !rig.IsTransmitting && !cat.Ptt && rig.OffCommands > 0),
        "STOP did not unkey the virtual radio");
      Assert.True(Until(() => cat.LastPttOffHardwareReadbackUtc >
        cat.LastPttOnHardwareReadbackUtc), "Missing post-STOP RX readback");
      int onCount = rig.OnCommands;
      Thread.Sleep(300);
      Assert.Equal(onCount, rig.OnCommands);
    }

    [Fact]
    public void VirtualRadio_CatDisconnectDuringKeyStillAllowsStopOnReconnect()
    {
      using var rig = new VirtualRig { DropAfterFirstKey = true };
      using var cat = StartCat(rig);
      cat.SetPtt(true);
      Assert.True(Until(() => rig.IsTransmitting && rig.OnCommands > 0),
        "Virtual radio never saw KEY before disconnect");
      // This STOP happens while the initial CAT ON ACK is lost. It must not
      // be overwritten by that in-flight ON or dropped during reconnect.
      cat.ReleaseApplicationPtt();
      Assert.True(Until(() => rig.Connections >= 2), "CAT worker did not reconnect");
      Assert.True(Until(() => !rig.IsTransmitting && rig.OffCommands > 0),
        "Queued STOP was lost after CAT disconnect");
      Assert.True(Until(() => !cat.Ptt &&
        cat.LastPttOffHardwareReadbackUtc > cat.LastPttOnHardwareReadbackUtc),
        "CAT worker did not confirm virtual RX after reconnect");
    }

    private static CatControlEngine StartCat(VirtualRig rig)
    {
      var previous = SynchronizationContext.Current;
      SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
      try
      {
        var cat = new CatControlEngine(
          new CatRadioSettings { Host = "127.0.0.1", Port = rig.Port },
          new CatSettings
          {
            Delay = 50, SendTimeout = 1200, ReceiveTimeout = 1200,
            ReconnectDelay = 100
          });
        cat.Start(rx: false, tx: true, crossband: false);
        Assert.True(Until(() => cat.IsRunning),
          $"Virtual CAT did not connect: accepted={rig.Connections}; commands={rig.Commands}; server={rig.LastError}");
        return cat;
      }
      finally
      {
        SynchronizationContext.SetSynchronizationContext(previous);
      }
    }

    private sealed class VirtualRig : IDisposable
    {
      // Model the radio's PTT as separate from CAT connection lifetime.
      private const string Caps =
        "{\"version\":1,\"model\":\"IC-9700\",\"cross_band_split\":false," +
        "\"simplex\":{\"when_receiving\":[\"read_ptt\",\"read_tx_frequency\",\"write_ptt_on\",\"write_ptt_off\"]," +
        "\"when_transmitting\":[\"read_ptt\",\"read_tx_frequency\",\"write_ptt_on\",\"write_ptt_off\"]," +
        "\"when_setting_up\":[]}}";
      private readonly TcpListener listener;
      private readonly CancellationTokenSource stop = new();
      private readonly Task server;
      private int keyed, onCommands, offCommands, connections, dropped;
      private readonly ConcurrentQueue<string> trace = new();
      public string Commands => string.Join(" | ", trace);
      public string? LastError { get; private set; }
      public bool DropAfterFirstKey { get; set; }
      public bool UseCrLf { get; set; }
      public bool IsTransmitting => Volatile.Read(ref keyed) != 0;
      public int OnCommands => Volatile.Read(ref onCommands);
      public int OffCommands => Volatile.Read(ref offCommands);
      public int Connections => Volatile.Read(ref connections);
      public ushort Port { get; }

      public VirtualRig()
      {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
        server = Task.Run(ServeAsync);
      }

      private async Task ServeAsync()
      {
        try
        {
          while (!stop.IsCancellationRequested)
          {
            using var client = await listener.AcceptTcpClientAsync(stop.Token);
            Interlocked.Increment(ref connections);
            try
            {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII,
              detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(stream, Encoding.ASCII,
              leaveOpen: true) { NewLine = UseCrLf ? "\r\n" : "\n", AutoFlush = true };
            while (!stop.IsCancellationRequested)
            {
              string? line = await reader.ReadLineAsync(stop.Token);
              if (line == null) break;
              trace.Enqueue(line);
              string response;
              bool closeConnection = false;
              switch (line)
              {
                case "a": response = Caps; break;
                case "t": response = IsTransmitting ? "1" : "0"; break;
                case "i": case "f": response = "435000000"; break;
                case "T 1":
                  Interlocked.Exchange(ref keyed, 1);
                  Interlocked.Increment(ref onCommands);
                  if (DropAfterFirstKey && Interlocked.Exchange(ref dropped, 1) == 0)
                  {
                    closeConnection = true; // emulate a lost ACK after real PTT ON
                    response = string.Empty;
                    break;
                  }
                  response = "RPRT 0"; break;
                case "T 0":
                  Interlocked.Exchange(ref keyed, 0);
                  Interlocked.Increment(ref offCommands);
                  response = "RPRT 0"; break;
                default: response = "RPRT 0"; break;
              }
              if (closeConnection) break;
              await writer.WriteLineAsync(response);
            }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
              // A radio-side CAT socket can be abruptly reset: that single
              // connection must not kill the virtual rig listener itself.
              if (!stop.IsCancellationRequested) LastError = ex.Message;
            }
          }
        }
        catch (Exception ex)
        {
          // Diagnostics remain available even when the virtual server dies.
          if (!stop.IsCancellationRequested) LastError = ex.ToString();
        }
      }

      public void Dispose()
      {
        stop.Cancel();
        listener.Stop();
        try { server.Wait(TimeSpan.FromSeconds(2)); } catch { }
        stop.Dispose();
      }
    }
  }
}
