using Serilog;
using VE3NEA;

namespace SkyRoof
{
  internal static class Program
  {
    [STAThread]
    static void Main(string[] args)
    {
      if(args.Length>0 && args[0]=="--skycat-worker")
      {
        SkyCatSubprocess.Run(args);
        return;
      }
      if(args.Length>0 && args[0]=="--rotator-engine-worker")
      {
        RotatorEngineSubprocess.RunChild(args);
        return;
      }
      if(args.Length>0 && args[0]=="--rotator-transport-worker")
      {
        SkyRoof.NativeRotator.IsolatedRotatorTransport.RunChild(args);
        return;
      }
      using (new SingleInstanceEnforcer())
      {
        try
        {
          ExceptionLogger.Initialize();
          Application.SetHighDpiMode(HighDpiMode.SystemAware); // compatibility with Wine 9
          Theme.Initialize(); // must run before any window is created
          ApplicationConfiguration.Initialize();
          Application.Run(new MainForm());
        }
        finally
        {
          Log.Information("Stopping SkyRoof\r\n\r\n-----------------------\r\n");
          Log.CloseAndFlush();
        }
      }
    }
  }
}