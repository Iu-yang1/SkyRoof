using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SkyRoof
{
  /// <summary>
  /// The entire SkyCAT CI-V and TCP server lives in a distinct SkyRoof.exe
  /// worker process. No third-party serial/CI-V/CW library is executed on
  /// the WinForms process. All lifecycle I/O is asynchronous and bounded.
  /// </summary>
  internal sealed class EmbeddedSkyCatHost : IDisposable
  {
    private readonly object gate=new();
    private Process? child;
    private NamedPipeServerStream? pipe;
    private string? activeKey;
    private bool ready,disposed,expectedExit,restartBlocked;
    internal event EventHandler? ReadinessChanged;

    internal bool IsRunning
    {
      get { lock(gate) return ready && child is { HasExited:false }; }
    }

    internal void Apply(EmbeddedSkyCatSettings settings)
    {
      if(disposed)return;
      if(!settings.Enabled || settings.Validate()!=null)
      {
        if(!settings.Enabled)lock(gate)restartBlocked=false;
        if(settings.Enabled)
          Log.Warning("Embedded SkyCAT worker not launched: {Reason}",settings.Validate());
        Stop();
        return;
      }
      string key=settings.ConfigurationKey();
      lock(gate)
      {
        if(restartBlocked)return;
        if(activeKey==key && child is { HasExited:false })return;
      }
      Stop();
      lock(gate)
      {
        // Never start a second owner of the serial port while a previous
        // worker remains alive. It must terminate before a new launch.
        if(child is { HasExited:false })
        {
          Log.Warning("Old SkyCAT child still exiting; refusing a second CI-V serial owner.");
          return;
        }
      }
      foreach(int port in ActivePorts(settings))
        if(!PortAvailable(port))
        {
          Log.Warning("Embedded SkyCAT cannot bind loopback port {Port}; not connecting to a foreign CAT server.",port);
          return;
        }

      NamedPipeServerStream? created=null;
      Process? process=null;
      try
      {
        string pipeName="SkyRoof-SkyCAT-"+Guid.NewGuid().ToString("N");
        created=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,
          PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        string encoded=Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings));
        var start=new ProcessStartInfo(Environment.ProcessPath ??
          throw new InvalidOperationException("Executable path not resolved"))
        {
          UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--skycat-worker");
        start.ArgumentList.Add(pipeName);
        start.ArgumentList.Add(encoded);
        process=Process.Start(start) ?? throw new IOException("SkyCAT worker process failed to launch");
        lock(gate)
        {
          child=process;pipe=created;activeKey=key;expectedExit=false;ready=false;
        }
        _=MonitorAsync(process,created);
        Log.Information("Started isolated SkyCAT child PID={Pid}. CAT and CI-V are no longer in SkyRoof's UI process.",process.Id);
      }
      catch(Exception ex)
      {
        created?.Dispose();
        if(process is { HasExited:false })try{process.Kill();}catch{}
        Log.Error(ex,"SkyCAT process launch failed");
      }
    }

    private async Task MonitorAsync(Process process,NamedPipeServerStream connection)
    {
      bool unexpected=false;
      try
      {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await connection.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
        var code=new byte[1];
        int n=await connection.ReadAsync(code,deadline.Token).ConfigureAwait(false);
        if(n!=1 || code[0]!=(byte)'R')
          throw new IOException("SkyCAT child rejected startup handshake");
        lock(gate)
        {
          if(ReferenceEquals(process,child) && !expectedExit)
          {
            ready=true;
            restartBlocked=false;
          }
        }
        ReadinessChanged?.Invoke(this,EventArgs.Empty);
        await process.WaitForExitAsync().ConfigureAwait(false);
      }
      catch(Exception ex)
      {
        Log.Warning(ex,"SkyCAT child control channel closed before normal shutdown");
        unexpected=true;
      }
      finally
      {
        bool wasExpected;
        lock(gate)
        {
          wasExpected=expectedExit;
          if(!wasExpected)restartBlocked=true;
          if(ReferenceEquals(process,child))
          {
            ready=false;
            activeKey=null;
            if(process.HasExited){child=null;pipe=null;}
          }
        }
        if(!process.HasExited)
        {
          try{process.Kill(entireProcessTree:true);}catch{}
        }
        connection.Dispose();
        process.Dispose();
        if(!wasExpected)
        {
          Log.Warning("SkyCAT child exited. CAT controls remain disabled. Turn Embedded SkyCAT off then on to recover after verifying radio PTT.");
          ReadinessChanged?.Invoke(this,EventArgs.Empty);
        }
      }
    }

    internal static IEnumerable<int> ActivePorts(EmbeddedSkyCatSettings s)
    {
      yield return s.CatPort;
      yield return s.SpectrumPort;
      if(s.WsjtXEnabled)yield return s.WsjtXPort;
      if(s.SwitchEnabled)yield return s.SwitchPort;
      if(s.CwEnabled)yield return s.CwPort;
    }

    internal static bool PortAvailable(int port)
    {
      if(port<=0 || port>65535)return false;
      TcpListener? probe=null;
      try {probe=new TcpListener(IPAddress.Loopback,port);probe.Start();return true;}
      catch(SocketException){return false;}
      catch(System.Security.SecurityException){return false;}
      finally{probe?.Stop();}
    }

    private void Stop()
    {
      Process? process;
      NamedPipeServerStream? connection;
      lock(gate)
      {
        process=child;connection=pipe;expectedExit=true;ready=false;
        activeKey=null;
      }
      if(process==null)return;
      try
      {
        if(connection?.IsConnected==true)
        {
          // A graceful EOF lets the child release SkyCAT-owned PTT/CW,
          // restore CI-V configuration, and close the serial port.
          connection.WriteByte((byte)'S');
          connection.Flush();
        }
      }
      catch(IOException){}
      catch(ObjectDisposedException){}
      // Never Wait/Join on a WinForms callback, including FormClosing.
      _=Task.Run(async () =>
      {
        try
        {
          if(!process.HasExited)
          {
            using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try{await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);}
            catch(OperationCanceledException)
            {
              Log.Error("SkyCAT child did not safely stop within 5s. Forcibly terminating; radio PTT must be checked.");
              try{process.Kill(entireProcessTree:true);}catch{}
            }
          }
        }
        catch(InvalidOperationException){}
      });
    }

    public void Dispose()
    {
      if(disposed)return;
      disposed=true;
      Stop();
    }
  }
}
