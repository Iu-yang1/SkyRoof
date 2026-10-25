using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SkyRoof.NativeRotator;

/// <summary>
/// An I/O-process boundary for *every* native serial/TCP/UDP rotator driver.
/// The motion scheduler and safety state stay on the normal NativeRotatorIO
/// background thread, but OS device handles and blocking Read/Write/Open
/// are confined to a separate executable PID.
/// </summary>
internal sealed class IsolatedRotatorTransport : IRotatorTransport
{
  private readonly RotatorSettings settings;
  private readonly string? workerExecutable;
  private readonly object sync=new();
  private Process? child;
  private NamedPipeServerStream? pipe;
  private BinaryReader? input;
  private BinaryWriter? output;
  private bool disposed;
  private int abortRequested;
  internal IsolatedRotatorTransport(RotatorSettings settings,
    string? workerExecutable=null)
  {
    this.settings=settings;
    this.workerExecutable=workerExecutable;
  }
  internal int? WorkerPid => child?.Id;
  internal static bool ShouldIsolateThisProcess() =>
    string.Equals(Path.GetFileName(Environment.ProcessPath),
      "SkyRoof.exe",StringComparison.OrdinalIgnoreCase);

  public bool Connected
  {
    get
    {
      lock(sync)
      {
        try{return !disposed && pipe?.IsConnected==true &&
          child is { HasExited:false };}
        catch(InvalidOperationException){return false;}
      }
    }
  }

  public void Open()
  {
    if(disposed)throw new ObjectDisposedException(nameof(IsolatedRotatorTransport));
    string id="SkyRoof-Rotator-"+Guid.NewGuid().ToString("N");
    NamedPipeServerStream? endpoint=null;
    Process? spawned=null;
    try
    {
      endpoint=new NamedPipeServerStream(id,PipeDirection.InOut,1,
        PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
      var args=new ProcessStartInfo(workerExecutable ?? Environment.ProcessPath ??
        throw new IOException("Rotator worker executable is unavailable"))
      {
        UseShellExecute=false,CreateNoWindow=true,
        WindowStyle=ProcessWindowStyle.Hidden
      };
      args.ArgumentList.Add("--rotator-transport-worker");
      args.ArgumentList.Add(id);
      args.ArgumentList.Add(Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(settings)));
      spawned=Process.Start(args) ??
        throw new IOException("Rotator worker launch failed");
      using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(7));
      endpoint.WaitForConnectionAsync(wait.Token).GetAwaiter().GetResult();
      endpoint.ReadTimeout=2500;
      endpoint.WriteTimeout=2500;
      var reader=new BinaryReader(endpoint);
      var writer=new BinaryWriter(endpoint);
      // OPEN happens entirely within child; a stuck vendor Open() cannot
      // occupy the parent WinForms process or crash its native heap.
      ReadStatus(reader);
      lock(sync)
      {
        if(disposed)throw new ObjectDisposedException(nameof(IsolatedRotatorTransport));
        pipe=endpoint;child=spawned;input=reader;output=writer;
      }
      Log.Information("Native rotator I/O isolated in child PID {Pid}",spawned.Id);
    }
    catch
    {
      endpoint?.Dispose();
      if(spawned is { HasExited:false })try{spawned.Kill(entireProcessTree:true);}catch{}
      spawned?.Dispose();
      throw;
    }
  }

  public void Write(ReadOnlySpan<byte> bytes)
  {
    lock(sync)
    {
      EnsureReady();
      if(bytes.Length>4096)throw new InvalidDataException("Motor frame too long");
      output!.Write((byte)1);
      output.Write(bytes.Length);
      output.Write(bytes);
      output.Flush();
      ReadStatus(input!);
    }
  }

  public int Read(byte[] buffer)
  {
    lock(sync)
    {
      EnsureReady();
      output!.Write((byte)2);
      output.Flush();
      ReadStatus(input!);
      int count=input!.ReadInt32();
      if(count<0 || count>buffer.Length || count>65536)
        throw new InvalidDataException("Motor child returned invalid frame size");
      int offset=0;
      while(offset<count)
      {
        int n=input.Read(buffer,offset,count-offset);
        if(n==0)throw new EndOfStreamException("Motor child pipe disconnected");
        offset+=n;
      }
      return count;
    }
  }

  private void EnsureReady()
  {
    if(!Connected)throw new EndOfStreamException(
      "Isolated rotator I/O process disconnected. New movement requires verified feedback.");
  }

  private static void ReadStatus(BinaryReader reader)
  {
    byte code=reader.ReadByte();
    if(code==0)return;
    if(code!=1)throw new InvalidDataException("Unexpected motor child response");
    throw new IOException("Rotator child I/O error: "+reader.ReadString());
  }

  public void Abort()
  {
    if(Interlocked.Exchange(ref abortRequested,1)!=0)return;
    disposed=true;
    // Close the OS pipe WITHOUT acquiring sync: the I/O thread can be
    // blocked inside Read/Write holding it while WinForms requests shutdown.
    NamedPipeServerStream? endpoint=Interlocked.Exchange(ref pipe,null);
    Process? process=Interlocked.Exchange(ref child,null);
    try{endpoint?.Dispose();}catch{}
    // Never synchronously wait for a native serial driver in the UI process.
    if(process!=null)
      _=Task.Run(async ()=>
      {
        try
        {
          using var cts=new CancellationTokenSource(TimeSpan.FromSeconds(2));
          try{await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);}
          catch(OperationCanceledException)
          {
            Log.Warning("Rotator child stuck in device I/O; terminating isolated PID {Pid}",process.Id);
            if(!process.HasExited)process.Kill(entireProcessTree:true);
          }
        }
        catch(Exception ex){Log.Warning(ex,"Rotator child termination failed");}
        finally{process.Dispose();}
      });
  }
  public void Dispose()=>Abort();

  /// <summary>Executed only from Program.Main's isolated child path.</summary>
  internal static void RunChild(string[] args)
  {
    if(args.Length!=3)return;
    IRotatorTransport? driver=null;
    try
    {
      RotatorSettings settings=JsonSerializer.Deserialize<RotatorSettings>(
        Convert.FromBase64String(args[2])) ??
        throw new InvalidDataException("Missing rotator configuration");
      using var client=new NamedPipeClientStream(".",args[1],
        PipeDirection.InOut,PipeOptions.Asynchronous);
      client.Connect(5000);
      using var reader=new BinaryReader(client);
      using var writer=new BinaryWriter(client);
      try
      {
        driver=NativeTransportFactory.CreateDirect(settings);
        driver.Open();
        writer.Write((byte)0);writer.Flush();
      }
      catch(Exception ex)
      {
        writer.Write((byte)1);writer.Write(ex.Message);writer.Flush();
        return;
      }
      while(client.IsConnected)
      {
        int command;
        try{command=reader.ReadByte();}
        catch(EndOfStreamException){break;}
        try
        {
          if(command==1)
          {
            int n=reader.ReadInt32();
            if(n<0 || n>4096)throw new InvalidDataException("Motor command length invalid");
            var bytes=reader.ReadBytes(n);
            if(bytes.Length!=n)throw new EndOfStreamException();
            driver.Write(bytes);
            writer.Write((byte)0);writer.Flush();
          }
          else if(command==2)
          {
            byte[] buffer=new byte[4096];
            int n=driver.Read(buffer);
            writer.Write((byte)0);
            writer.Write(n);
            writer.Write(buffer,0,n);
            writer.Flush();
          }
          else throw new InvalidDataException("Unknown motor IPC opcode");
        }
        catch(Exception ex)
        {
          try{writer.Write((byte)1);writer.Write(ex.Message);writer.Flush();}
          catch(IOException){break;}
          catch(ObjectDisposedException){break;}
        }
      }
    }
    catch(Exception){}
    finally
    {
      // If SkyRoof disappears, a surviving motor may keep moving; attempt
      // protocol STOP from the child before releasing its hardware handle.
      if(driver!=null)
      {
        try
        {
          var s=JsonSerializer.Deserialize<RotatorSettings>(
            Convert.FromBase64String(args[2]));
          if(s!=null)
          {
            byte[]? stop=NativeRotatorCodec.Stop(s.NativeProtocol,s.NativeStopTemplate);
            if(stop!=null && driver.Connected)driver.Write(stop);
          }
        }
        catch(Exception){}
        try{driver.Dispose();}catch(Exception){}
      }
    }
  }
}
