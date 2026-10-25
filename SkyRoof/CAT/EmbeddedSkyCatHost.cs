using System;
using System.Threading.Tasks;
using Serilog;
using Serilog.Extensions.Logging;
using skycatd;

namespace SkyRoof
{
  /// <summary>
  /// In-process SkyCAT service with strictly ordered shutdown:
  /// CW/PTT SkyRoof consumers leave first, then this host releases its
  /// owner-specific leases and the single shared CI-V serial port.
  /// </summary>
  internal sealed class EmbeddedSkyCatHost : IDisposable
  {
    private CatServer? server;
    private Task? runTask;
    private SerilogLoggerFactory? logFactory;
    private string? activeKey;
    private bool disposed;
    internal bool IsRunning => runTask is { IsCompleted: false };

    internal void Apply(EmbeddedSkyCatSettings settings)
    {
      if(disposed)return;
      if(!settings.Enabled)
      {
        Stop();
        return;
      }
      string? error=settings.Validate();
      if(error!=null)
      {
        Log.Warning("Embedded SkyCAT disabled: {Reason}",error);
        Stop();
        return;
      }
      string key=settings.ConfigurationKey();
      if(activeKey==key && IsRunning)return;
      if(!Stop())return;
      try
      {
        var factory=new SerilogLoggerFactory(Log.Logger);
        var candidate=new CatServer(settings.BuildOptions(),
          factory.CreateLogger("SkyRoof.EmbeddedSkyCAT"));
        logFactory=factory;
        server=candidate;
        activeKey=key;
        runTask=Task.Run(() =>
        {
          try { candidate.Run(); }
          catch(Exception ex)
          {
            Log.Error(ex,"Embedded SkyCAT server stopped unexpectedly.");
          }
        });
        Log.Information(
          "Embedded SkyCAT started: {Model}, serial {Port}; CAT 127.0.0.1:{CatPort}; WSJT-X {Wsjt}; Scope {Scope}; Switch {Switch}; CW {Cw}",
          settings.Model,settings.SerialPort,settings.CatPort,
          settings.WsjtXEnabled?settings.WsjtXPort:0,
          settings.SpectrumPort,settings.SwitchEnabled?settings.SwitchPort:0,
          settings.CwEnabled?settings.CwPort:0);
      }
      catch(Exception ex)
      {
        Log.Error(ex,"Unable to start embedded SkyCAT.");
        Stop();
      }
    }

    private bool Stop()
    {
      if(server==null && runTask==null)return true;
      try
      {
        server?.Stop();
        if(runTask!=null && !runTask.Wait(TimeSpan.FromSeconds(10)))
        {
          Log.Warning("Embedded SkyCAT shutdown is incomplete; refusing to start a second serial owner.");
          return false;
        }
      }
      catch(Exception ex)
      {
        Log.Warning(ex,"Embedded SkyCAT shutdown encountered an exception.");
        if(runTask is { IsCompleted: false })return false;
      }
      finally
      {
        if(runTask==null || runTask.IsCompleted)
        {
          server=null;
          runTask=null;
          activeKey=null;
          logFactory?.Dispose();
          logFactory=null;
        }
      }
      return true;
    }

    public void Dispose()
    {
      if(disposed)return;
      disposed=true;
      Stop();
    }
  }
}
