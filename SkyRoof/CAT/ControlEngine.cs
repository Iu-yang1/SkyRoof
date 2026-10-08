using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Serilog;

namespace SkyRoof
{
  public abstract class ControlEngine : IDisposable
  {
    protected SynchronizationContext syncContext = SynchronizationContext.Current!;

    public readonly string Host;
    protected readonly ushort Port;
    protected readonly int Delay;
    protected readonly bool log;

    protected Thread? processingThread;
    protected TcpClient? TcpClient;
    protected bool stopping = false;
    protected bool ErrorLogged = false;
    private readonly int sendTimeout;
    private readonly int receiveTimeout;
    private readonly int reconnectDelay;
    private readonly ManualResetEventSlim stopEvent = new ManualResetEventSlim(false);
    private readonly byte[] receiveBuffer = new byte[4096];
    private readonly StringBuilder receivePending = new();

    public event EventHandler? StatusChanged;
    public bool IsRunning {get; private set;}


    public ControlEngine(string host, ushort port, IControlEngineSettings settings)
    {
      Host = host;
      Port = port;
      Delay = settings.Delay;
      log = settings.LogTraffic;
      sendTimeout = settings.SendTimeout;
      receiveTimeout = settings.ReceiveTimeout;
      reconnectDelay = settings.ReconnectDelay;
    }




    //----------------------------------------------------------------------------------------------
    //                                        thread 
    //----------------------------------------------------------------------------------------------
    internal void Retry()
    {
      if (processingThread == null) StartThread();
    }
    
    protected void StartThread()
    {
      if (processingThread != null) return;

      stopping = false;
      stopEvent.Reset();
      processingThread = new Thread(new ThreadStart(ThreadProcedure));
      processingThread.IsBackground = true;
      processingThread.Name = GetType().Name;
      processingThread.Start();
    }

    protected void StopThread()
    {
      if (stopping) return;
      stopping = true;
      stopEvent.Set();

      // Connect/Receive are synchronous socket calls. Closing the current client
      // from the stopping thread interrupts either operation so UI shutdown does
      // not have to wait for an OS/TCP timeout before Join can complete.
      try { TcpClient?.Close(); } catch { }

      processingThread?.Join();
      processingThread = null;
    }

    private void ThreadProcedure()
    {
      processingThread!.Priority = ThreadPriority.Highest;

      bool lastReportedRunning = false;
      bool everConnected = false;

      while (!stopping)
      {
        if (Connect() && Setup())
        {
          IsRunning = true;
          ErrorLogged = false;
          everConnected = true;
          if (!lastReportedRunning) { lastReportedRunning = true; OnStatusChanged(); }

          while (!stopping && TcpClient != null && TcpClient.Connected)
            try
            {
              Cycle();
              Thread.Sleep(Delay);
            }
            catch (SocketException ex)
            {
              Log.Error(ex, $"Socket error in {GetType().Name}");
              break;
            }
            catch (Exception ex)
            {
              Log.Error(ex, $"Error in {GetType().Name}");
            }
        }

        Disconnect();
        IsRunning = false;
        if (lastReportedRunning) { lastReportedRunning = false; OnStatusChanged(); }

        if (!stopping)
        {
          string state = everConnected ? "connection lost" : "initial connection failed";
          Log.Information($"{GetType().Name}: {state}, retrying in {reconnectDelay / 1000} seconds");
          stopEvent.Wait(reconnectDelay);
        }
      }

      processingThread = null;
    }

    protected abstract bool Setup();
    protected abstract void Cycle();




    //----------------------------------------------------------------------------------------------
    //                                    connection
    //----------------------------------------------------------------------------------------------
    private bool Connect()
    {
      TcpClient = new();
      TcpClient.SendTimeout = sendTimeout;
      TcpClient.ReceiveTimeout = receiveTimeout;

      try
      {
        Log.Information($"Connecting to {Host}:{Port}");
        TcpClient!.Connect(Host, Port);
        Log.Information($"Connected to {Host}:{Port}");
        return true;
      }
      catch (SocketException ex)
      {
        if (!stopping && !ErrorLogged)
        {
          ErrorLogged = true;
          Log.Error(ex, $"Unable to connect to {Host}:{Port}");
        }
        return false;
      }
      catch (ObjectDisposedException) when (stopping)
      {
        // StopThread closed the socket to interrupt a blocking Connect.
        return false;
      }
    }

    protected void Disconnect()
    {
      try
      {
        if (TcpClient != null)
        {
          Log.Information($"Disconnecting from {Host}:{Port}");
          if (TcpClient.Connected) TcpClient.Client.Shutdown(SocketShutdown.Both);
          TcpClient.Close();
          Log.Information($"Disconnected from {Host}:{Port}");
        }
      }
      catch (Exception ex)
      {
        Log.Error(ex, $"Error while disconnecting from {Host}:{Port}");
      }
      finally
      {
        TcpClient?.Dispose(); // Ensure resources are released
        TcpClient = null; // Reset the TcpClient reference
        receivePending.Clear();
      }
    }




    //----------------------------------------------------------------------------------------------
    //                                       status
    //----------------------------------------------------------------------------------------------

    // send notification to the UI thread asynchronously
    protected void OnStatusChanged()
    {
      syncContext.Post(s => StatusChanged?.Invoke(this, EventArgs.Empty), null);
    }

    public string GetStatusString()
    {
      return IsRunning ? "Connected" : "Not connected";
    }




    //----------------------------------------------------------------------------------------------
    //                                       commands
    //----------------------------------------------------------------------------------------------
    protected bool SendWriteCommands(string[]? commands)
    {
      bool ok = true;

      foreach (string cmd in commands!)
      {
        // Do not short-circuit the sequence after one rejected command. Later
        // setup/cleanup commands can still leave the rig in the intended state.
        bool commandOk = SendWriteCommand(cmd);
        ok &= commandOk;
      }

      return ok;
    }

    protected bool SendWriteCommand(string command)
    {
      var reply = SendCommand(command);
      if (reply == "RPRT 0\n") return true;
      BadReply(reply);
      return false;
    }

    protected string? SendReadCommand(string command)
    {
      var reply = SendCommand(command);
      if (reply.EndsWith("\n"))
      {
        reply = reply.Substring(0, reply.Length - 1);
        if (log) Log.Information($"Reply from {GetType().Name} ctld: {reply}");
        return reply;
      }

      BadReply(reply);
      return null;
    }

    protected string SendCommand(string command)
    {
      try
      {
        if (log) Log.Information($"Sending command to {GetType().Name} ctld: {command}");
        byte[] commandBytes = Encoding.ASCII.GetBytes(command + "\n");
        TcpClient!.Client.Send(commandBytes);
      }
      catch (Exception ex)
      {
        Log.Error(ex, $"Failed to send command to {GetType().Name} ctld");
        throw;
      }

      try
      {
        return ReadLine();
      }
      catch (Exception ex)
      {
        Log.Error(ex, $"Failed to receive reply from {GetType().Name} ctld");
        throw;
      }
    }

    protected void BadReply(string reply)
    {
      Log.Error($"Unexpected reply from {GetType().Name} ctld: {reply.Trim()}");
    }

    protected string ReadLine()
    {
      const int MaxBufferedReply = 65536;

      while (true)
      {
        string pending = receivePending.ToString();
        int newline = pending.IndexOf('\n');
        if (newline >= 0)
        {
          string line = pending[..(newline + 1)];
          receivePending.Remove(0, newline + 1);
          return line;
        }

        if (receivePending.Length >= MaxBufferedReply)
        {
          string oversized = receivePending.ToString();
          receivePending.Clear();
          return oversized;
        }

        int bytesRead = TcpClient!.Client.Receive(
          receiveBuffer,
          0,
          receiveBuffer.Length,
          SocketFlags.None);

        if (bytesRead == 0)
        {
          // EOF is a closed TCP session, not a valid reply. Surface it through
          // the existing SocketException -> reconnect path instead of spinning
          // on an apparently connected socket with empty replies.
          receivePending.Clear();
          throw new SocketException((int)SocketError.ConnectionReset);
        }

        // TCP is a byte stream: one Receive may contain several rigctld
        // replies, or only part of one. Keep all bytes after the first newline
        // for the next command instead of dropping a coalesced reply.
        receivePending.Append(Encoding.ASCII.GetString(receiveBuffer, 0, bytesRead));
      }
    }




    //----------------------------------------------------------------------------------------------
    //                                     IDisposable
    //----------------------------------------------------------------------------------------------
    public virtual void Dispose()
    {
      StopThread();
      stopEvent.Dispose();
    }
  }
}
