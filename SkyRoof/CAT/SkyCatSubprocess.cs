using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Serilog;
using Serilog.Extensions.Logging;
using skycatd;
using VE3NEA;

namespace SkyRoof
{
  internal static class SkyCatSubprocess
  {
    internal static void Run(string[] args)
    {
      if(args.Length!=3)return;
      try
      {
        // This branch runs BEFORE the single-instance guard or WinForms
        // initialization. No MainForm/SoapySDR/renderer is constructed.
        var config=JsonSerializer.Deserialize<EmbeddedSkyCatSettings>(
          Convert.FromBase64String(args[2])) ??
          throw new InvalidDataException("Invalid SkyCAT child config");
        if(!config.Enabled || config.Validate()!=null)
          throw new InvalidDataException("SkyCAT child config rejected");
        using var parent=new NamedPipeClientStream(".",args[1],
          PipeDirection.InOut,PipeOptions.Asynchronous);
        parent.Connect(4000);
        Directory.CreateDirectory(Utils.GetLogArchiveFolder());
        Log.Logger=new LoggerConfiguration().MinimumLevel.Information()
          .WriteTo.File(Path.Combine(Utils.GetLogArchiveFolder(),"SkyCAT-Worker_.txt"),
            rollingInterval:RollingInterval.Day,
            rollOnFileSizeLimit:true,fileSizeLimitBytes:3_000_000,
            retainedFileCountLimit:10,shared:true).CreateLogger();
        using var factory=new SerilogLoggerFactory(Log.Logger);
        var server=new CatServer(config.BuildOptions(),
          factory.CreateLogger("SkyRoof.SkyCAT.Child"));
        Task runner=Task.Run(()=>server.Run());
        parent.WriteByte((byte)'R');
        parent.Flush();
        // The pipe is owned by the parent. EOF, intentional stop, or parent
        // crash always causes a bounded attempt to release the radio.
        int signal=parent.ReadByte();
        Log.Information("SkyCAT parent control signal: {Signal}",signal);
        server.Stop();
        try{runner.Wait(TimeSpan.FromSeconds(4));}
        catch(AggregateException ex){Log.Warning(ex,"SkyCAT shutdown exception");}
      }
      catch(Exception ex){try{Log.Error(ex,"SkyCAT subprocess failed");}catch{}}
      finally {Log.CloseAndFlush();}
    }
  }
}
