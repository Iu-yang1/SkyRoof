using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using SkyRoof.NativeRotator;
using VE3NEA;

namespace SkyRoof;

/// <summary>
/// Full native motion/PARK/safety engine in a second PID. The parent exposes
/// only cached telemetry and non-blocking command submissions to WinForms.
/// </summary>
internal sealed class RotatorEngineSubprocess : INativeRotatorEngine
{
  internal static bool InChildEngine {get;private set;}
  internal int WorkerPid => child.Id;
  internal static bool ShouldHostWorker() =>
    !InChildEngine && string.Equals(
      System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name,
      "SkyRoof",StringComparison.OrdinalIgnoreCase);

  private readonly SynchronizationContext ui;
  private readonly object sync=new();
  private readonly Process child;
  private readonly NamedPipeServerStream pipe;
  private readonly CancellationTokenSource cancel=new();
  private readonly SemaphoreSlim commandsAvailable=new(0);
  // Motion commands are coalesced; administrative commands have a strict
  // upper bound. STOP is inserted at the FRONT, never behind buffered jogs.
  private readonly Queue<RotatorWireMessage> controlQueue=new();
  private const int MaxQueuedControls=64;
  private int writerWakePending;
  private readonly ConcurrentDictionary<long,TaskCompletionSource<bool>> pending=new();
  private RotatorWireMessage? newestTarget;
  private bool disposed,connected,ready,faulted=true,review;
  private long nextId,readTicks,targetTicks,stopTicks,rateTicks;
  private double? az,el,requestAz,requestEl,writtenAz,writtenEl,rateAz,rateEl;
  private string status="Starting isolated rotator controller";
  public event EventHandler? StatusChanged;
  public event EventHandler? BearingChanged;
  public event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;

  internal RotatorEngineSubprocess(RotatorSettings settings,
    string? workerExecutable=null)
  {
    ui=SynchronizationContext.Current??new SynchronizationContext();
    string name="SkyRoof-RotatorEngine-"+Guid.NewGuid().ToString("N");
    pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,
      PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
    try
    {
      var start=new ProcessStartInfo(workerExecutable ??
        Environment.ProcessPath ?? throw new IOException("Rotator apphost unavailable"))
      {
        UseShellExecute=false,CreateNoWindow=true,
        WindowStyle=ProcessWindowStyle.Hidden
      };
      start.ArgumentList.Add("--rotator-engine-worker");
      start.ArgumentList.Add(name);
      start.ArgumentList.Add(Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(settings)));
      child=Process.Start(start) ?? throw new IOException("Rotator engine subprocess launch failed");
      _=SuperviseAsync();
      Log.Information("Full rotator motion/safety controller runs in PID {Pid}",child.Id);
    }
    catch
    {
      pipe.Dispose();
      throw;
    }
  }

  public Bearing? RequestedBearing
  {get {lock(sync)return ToBearing(requestAz,requestEl);}}
  public Bearing? LastReadBearing
  {get {lock(sync)return ToBearing(az,el);}}
  public Bearing? LastWrittenBearing
  {get {lock(sync)return ToBearing(writtenAz,writtenEl);}}
  public DateTime LastSuccessfulBearingReadUtc =>
    new(Interlocked.Read(ref readTicks),DateTimeKind.Utc);
  public DateTime LastParkTargetWriteUtc =>
    new(Interlocked.Read(ref targetTicks),DateTimeKind.Utc);
  public DateTime LastParkStopWriteUtc =>
    new(Interlocked.Read(ref stopTicks),DateTimeKind.Utc);
  public Rc3040sFeedbackEstimator.Observation? LatestObservedRate
  {
    get
    {
      lock(sync)return rateAz.HasValue && rateEl.HasValue && rateTicks>0
        ? new Rc3040sFeedbackEstimator.Observation(
          rateAz.Value,rateEl.Value,new DateTime(rateTicks,DateTimeKind.Utc))
        : null;
    }
  }
  private bool ChildExited
  {
    get
    {
      try{return child.HasExited;}
      catch(InvalidOperationException){return true;}
      catch(ObjectDisposedException){return true;}
    }
  }
  public bool IsRunning
  {get {lock(sync)return !disposed && connected && !ChildExited;}}
  public bool IsReady
  {get {lock(sync)return !disposed && connected && ready && !ChildExited;}}
  public bool HasLiveConnection => IsRunning && IsReady;
  public bool PositionFaulted
  {get {lock(sync)return faulted || disposed || ChildExited;}}
  public bool ManagementReviewRequired
  {get {lock(sync)return review;}}
  public string GetStatusString()
  {lock(sync)return ChildExited
    ? "Rotator worker exited; STOP unverified. Reapply settings."
    : status;}

  private static Bearing? ToBearing(double? az,double? el) =>
    az.HasValue && el.HasValue
      ? new Bearing(az.Value*Math.PI/180,el.Value*Math.PI/180) : null;

  public void RotateTo(Bearing bearing)
  {
    if(!double.IsFinite(bearing.AzDeg)||!double.IsFinite(bearing.ElDeg))return;
    lock(sync)
    {
      if(disposed || ChildExited || faulted || !ready || !connected)return;
      requestAz=bearing.AzDeg;requestEl=bearing.ElDeg;
      newestTarget=new RotatorWireMessage{
        Type="move",Az=bearing.AzDeg,El=bearing.ElDeg};
    }
    SignalWriter();
  }
  public void StartContinuousMove(RotatorContinuousDirection direction)
  {
    if(PositionFaulted || !IsReady)return;
    lock(sync)newestTarget=null;
    Enqueue("jog",direction:(int)direction);
  }
  public void StopRotation()
  {
    // Collapse buffered manual/jog/retry commands before queueing STOP.
    // A stopped motor must never be restarted by stale operator input.
    Enqueue("stop");
  }
  public void Retry(){if(!IsReady)Enqueue("retry");}
  public void SetParkDiagnostics(bool active)=>Enqueue("diagnostics",flag:active);
  public void LatchAfterManagementChange()
  {
    lock(sync){faulted=true;review=true;newestTarget=null;}
    Enqueue("latch");NotifyStatus();
  }

  public async Task<IDisposable?> AcquireManagementLeaseAsync(
    CancellationToken cancellationToken=default)
  {
    if(!IsReady || PositionFaulted)return null;
    bool success=await RequestAsync("lease",4500,cancellationToken)
      .ConfigureAwait(false);
    if(!success)Enqueue("release");
    return success ? new RemoteLease(this) : null;
  }
  public Task<bool> VerifyPostManagementFeedbackAsync(
    CancellationToken cancellationToken=default) =>
    RequestAsync("verify",5500,cancellationToken);

  private sealed class RemoteLease : IDisposable
  {
    private RotatorEngineSubprocess? host;
    internal RemoteLease(RotatorEngineSubprocess host)=>this.host=host;
    public void Dispose()=>Interlocked.Exchange(ref host,null)?.Enqueue("release");
  }

  private async Task<bool> RequestAsync(string type,int timeout,
    CancellationToken cancellationToken)
  {
    if(!IsRunning)return false;
    long id=Interlocked.Increment(ref nextId);
    var completion=new TaskCompletionSource<bool>(
      TaskCreationOptions.RunContinuationsAsynchronously);
    if(!pending.TryAdd(id,completion))return false;
    Enqueue(type,id);
    try
    {
      return await completion.Task.WaitAsync(
        TimeSpan.FromMilliseconds(timeout),cancellationToken)
        .ConfigureAwait(false);
    }
    catch(OperationCanceledException){return false;}
    catch(TimeoutException){return false;}
    finally{pending.TryRemove(id,out _);}
  }

  internal int PendingControlCommands
  {get{lock(sync)return controlQueue.Count;}}

  private void Enqueue(string type,long id=0,bool flag=false,int direction=0)
  {
    bool overflow=false;
    lock(sync)
    {
      if(disposed)return;
      var item=new RotatorWireMessage{
        Type=type,Id=id,Flag=flag,Direction=direction};
      if(type is "stop" or "latch")
      {
        requestAz=requestEl=null;
        newestTarget=null;
        // An emergency stop gets ahead of *all* queued management
        // requests, but retains release/verify/lease transactions so
        // their owners can fail or complete in a defined order.
        var retained=controlQueue
          .Where(m=>m.Type is "lease" or "release" or "verify" or "latch")
          .Take(MaxQueuedControls-2).ToArray();
        controlQueue.Clear();
        controlQueue.Enqueue(new RotatorWireMessage{Type="stop"});
        if(type=="latch")controlQueue.Enqueue(item);
        foreach(var saved in retained)
          if(saved.Type!="latch" || type!="latch")
            controlQueue.Enqueue(saved);
      }
      else
      {
        if(type is "jog" or "retry" or "diagnostics")
        {
          // Wheel hold/retry/diagnostics updates are state, not a backlog.
          var keep=controlQueue.Where(m=>m.Type!=type).ToArray();
          controlQueue.Clear();
          foreach(var m in keep)controlQueue.Enqueue(m);
          if(type=="jog")newestTarget=null;
        }
        if(controlQueue.Count>=MaxQueuedControls)
        {
          // Any queue overflow makes motion unsafe. Latch and send STOP;
          // never allow the IPC queue to consume unbounded memory.
          controlQueue.Clear();
          newestTarget=null;
          requestAz=requestEl=null;
          faulted=true;review=true;
          status="Rotator IPC overload: STOP requested; reapply settings after inspection.";
          controlQueue.Enqueue(new RotatorWireMessage{Type="stop"});
          controlQueue.Enqueue(new RotatorWireMessage{Type="latch"});
          overflow=true;
        }
        else controlQueue.Enqueue(item);
      }
    }
    SignalWriter();
    if(overflow)
    {
      Log.Error("Rotator control queue overflow; no further motion allowed");
      NotifyStatus();
      foreach(var result in pending.Values)result.TrySetResult(false);
    }
  }

  private void SignalWriter()
  {
    // One wake means "queue changed", not one semaphore permit per message.
    if(Interlocked.Exchange(ref writerWakePending,1)==0)
      commandsAvailable.Release();
  }

  private async Task SuperviseAsync()
  {
    try
    {
      using var connectionDeadline=new CancellationTokenSource(
        TimeSpan.FromSeconds(7));
      await pipe.WaitForConnectionAsync(connectionDeadline.Token)
        .ConfigureAwait(false);
      using var reader=new StreamReader(pipe,Encoding.UTF8,
        detectEncodingFromByteOrderMarks:false,leaveOpen:true);
      using var writer=new StreamWriter(pipe,new UTF8Encoding(false),
        leaveOpen:true){AutoFlush=true};
      var writerTask=WriteCommandsAsync(writer);
      while(!cancel.IsCancellationRequested)
      {
        string? line=await reader.ReadLineAsync(cancel.Token)
          .ConfigureAwait(false);
        if(line==null)break;
        RotatorWireMessage? m=JsonSerializer.Deserialize<RotatorWireMessage>(line);
        if(m==null)continue;
        if(m.Type=="state")ApplyState(m);
        else if(m.Type=="reply" && pending.TryRemove(m.Id,out var reply))
          reply.TrySetResult(m.Flag);
        else if(m.Type=="fault")ApplyFault(m);
      }
      cancel.Cancel();SignalWriter();
      try{await writerTask.ConfigureAwait(false);}catch{}
    }
    catch(Exception ex) when(ex is not StackOverflowException)
    {
      if(!disposed)Log.Warning(ex,"Isolated rotator control channel failed");
    }
    finally
    {
      lock(sync)
      {
        connected=ready=false;
        faulted=true;
        review=true;
        status="Rotator control child offline; hardware STOP unverified. Reapply settings after inspecting rotor.";
        newestTarget=null;
      }
      foreach(var result in pending.Values)result.TrySetResult(false);
      pending.Clear();
      NotifyStatus();
      try{pipe.Dispose();}catch{}
      // Unexpected child death cannot safely trigger automatic reconnect.
      // No new motion can be queued until explicit driver reinitialization.
      _=Task.Run(async ()=>
      {
        try
        {
          if(!child.HasExited)
          {
            using var wait=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try{await child.WaitForExitAsync(wait.Token);}catch(OperationCanceledException)
            {if(!child.HasExited)child.Kill(entireProcessTree:true);}
          }
        }
        catch(Exception ex){Log.Warning(ex,"Failed to terminate rotator child");}
        finally{child.Dispose();}
      });
    }
  }

  private async Task WriteCommandsAsync(StreamWriter writer)
  {
    while(!cancel.IsCancellationRequested)
    {
      await commandsAvailable.WaitAsync(cancel.Token).ConfigureAwait(false);
      Interlocked.Exchange(ref writerWakePending,0);
      while(!cancel.IsCancellationRequested)
      {
        RotatorWireMessage? command;
        lock(sync)
        {
          // Controls (including STOP/latch) always precede track targets.
          command=controlQueue.Count>0 ? controlQueue.Dequeue() : newestTarget;
          if(controlQueue.Count==0 && ReferenceEquals(command,newestTarget))
            newestTarget=null;
        }
        if(command==null)break;
        await writer.WriteLineAsync(JsonSerializer.Serialize(command))
          .ConfigureAwait(false);
      }
    }
  }

  private void ApplyState(RotatorWireMessage m)
  {
    bool changed;
    bool posChanged;
    lock(sync)
    {
      if(disposed)return;
      posChanged=az!=m.Az||el!=m.El;
      changed=connected!=m.Running || ready!=m.Ready ||
        faulted!=m.Faulted || review!=m.Review;
      connected=m.Running;ready=m.Ready;faulted=m.Faulted;review=m.Review;
      az=m.Az;el=m.El;requestAz=m.RequestAz;requestEl=m.RequestEl;
      writtenAz=m.WrittenAz;writtenEl=m.WrittenEl;
      rateAz=m.RateAz;rateEl=m.RateEl;rateTicks=m.RateTicks;
      Interlocked.Exchange(ref readTicks,m.ReadTicks);
      Interlocked.Exchange(ref targetTicks,m.TargetTicks);
      Interlocked.Exchange(ref stopTicks,m.StopTicks);
      status=m.Status??"Rotator status unavailable";
    }
    if(changed)NotifyStatus();
    if(posChanged)ui.Post(_=>{if(!disposed)BearingChanged?.Invoke(this,EventArgs.Empty);},null);
  }

  private void ApplyFault(RotatorWireMessage m)
  {
    lock(sync){faulted=true;ready=false;}
    var prev=ToBearing(m.Az,m.El);
    var reported=ToBearing(m.Az2,m.El2);
    if(prev!=null && reported!=null)
      ui.Post(_=>
      {
        if(!disposed)PositionFaultDetected?.Invoke(this,
          new RotatorPositionFaultEventArgs(prev,reported,
            Math.Abs(prev.AzDeg-reported.AzDeg),
            Math.Abs(prev.ElDeg-reported.ElDeg),false));
      },null);
    NotifyStatus();
  }
  private void NotifyStatus()=>
    ui.Post(_=>{if(!disposed)StatusChanged?.Invoke(this,EventArgs.Empty);},null);

  public void Dispose()
  {
    if(disposed)return;
    // STOP has priority over queued motion; do not block WinForms waiting
    // for an unreliable USB/serial driver or unresponsive child.
    StopRotation();
    disposed=true;
    _=Task.Run(async ()=>
    {
      try
      {
        await Task.Delay(200);
        cancel.Cancel();
        pipe.Dispose(); // EOF triggers emergency STOP in worker
      }
      catch(Exception){}
    });
  }

  private static FileStream AcquireExclusiveDeviceOwner(RotatorSettings settings)
  {
    string identity=settings.NativeTransport==RotatorTransportKind.Serial
      ? "serial:"+settings.NativeSerialPort.Trim().ToUpperInvariant()
      : settings.NativeTransport.ToString()+":"+settings.Host.Trim().ToLowerInvariant()+
        ":"+settings.Port;
    string fingerprint=Convert.ToHexString(
      System.Security.Cryptography.SHA256.HashData(
        Encoding.UTF8.GetBytes(identity)));
    string directory=Path.Combine(Utils.GetUserDataFolder(),"WorkerLocks");
    Directory.CreateDirectory(directory);
    string path=Path.Combine(directory,"rotator-"+fingerprint+".lock");
    try
    {
      return new FileStream(path,FileMode.OpenOrCreate,
        FileAccess.ReadWrite,FileShare.None);
    }
    catch(IOException ex)
    {
      throw new IOException("Rotator hardware endpoint is already owned by another SkyRoof child process; refusing a second controller.",ex);
    }
  }

  internal static void RunChild(string[] args) =>
    RunChildAsync(args).GetAwaiter().GetResult();

  private static async Task RunChildAsync(string[] args)
  {
    if(args.Length!=3)return;
    InChildEngine=true;
    // This child has its own PID and logging sink; do not initialize the
    // parent WinForms exception handler or global radio/UI resources.
    Directory.CreateDirectory(Utils.GetLogArchiveFolder());
    Log.Logger=new LoggerConfiguration().MinimumLevel.Debug()
      .WriteTo.File(Path.Combine(Utils.GetLogArchiveFolder(),
        "RotatorEngine-Worker_.txt"),
        rollingInterval:RollingInterval.Day,
        fileSizeLimitBytes:3_000_000,rollOnFileSizeLimit:true,
        retainedFileCountLimit:10,shared:true).CreateLogger();
    NativeRotatorControlEngine? native=null;
    IDisposable? lease=null;
    try
    {
      var settings=JsonSerializer.Deserialize<RotatorSettings>(
        Convert.FromBase64String(args[2])) ??
        throw new InvalidDataException("Missing rotator engine configuration");
      // Two generations of the motion engine must NEVER command the same
      // hardware simultaneously, even if a user changes settings quickly.
      // The OS releases this cross-process file lock after a process crash.
      using var hardwareOwner=AcquireExclusiveDeviceOwner(settings);
      using var connection=new NamedPipeClientStream(".",args[1],
        PipeDirection.InOut,PipeOptions.Asynchronous);
      connection.Connect(4500);
      using var reader=new StreamReader(connection,Encoding.UTF8,
        detectEncodingFromByteOrderMarks:false,leaveOpen:true);
      using var writer=new StreamWriter(connection,new UTF8Encoding(false),
        leaveOpen:true){AutoFlush=true};
      using var writeLock=new SemaphoreSlim(1,1);
      async Task Publish(RotatorWireMessage m)
      {
        await writeLock.WaitAsync();
        try{await writer.WriteLineAsync(JsonSerializer.Serialize(m));}
        finally{writeLock.Release();}
      }
      // No Form/WinDivert/OpenGL/RS-BA1 renderer is constructed in this PID.
      native=new NativeRotatorControlEngine(settings);
      native.PositionFaultDetected+=(_,e)=>
      {
        _=Publish(new RotatorWireMessage{
          Type="fault",Az=e.Previous.AzDeg,El=e.Previous.ElDeg,
          Az2=e.Reported.AzDeg,El2=e.Reported.ElDeg});
      };
      var engine=native;
      using var clock=new CancellationTokenSource();
      var states=Task.Run(async ()=>
      {
        try
        {
          while(!clock.IsCancellationRequested)
          {
            var r=engine.LatestObservedRate;
            await Publish(new RotatorWireMessage{
              Type="state",
              Running=engine.IsRunning,Ready=engine.IsReady,
              Faulted=engine.PositionFaulted,
              Review=engine.ManagementReviewRequired,
              Az=engine.LastReadBearing?.AzDeg,
              El=engine.LastReadBearing?.ElDeg,
              RequestAz=engine.RequestedBearing?.AzDeg,
              RequestEl=engine.RequestedBearing?.ElDeg,
              WrittenAz=engine.LastWrittenBearing?.AzDeg,
              WrittenEl=engine.LastWrittenBearing?.ElDeg,
              RateAz=r?.AzDegPerSec,RateEl=r?.ElDegPerSec,
              RateTicks=r?.TimestampUtc.Ticks??0,
              ReadTicks=engine.LastSuccessfulBearingReadUtc.Ticks,
              TargetTicks=engine.LastParkTargetWriteUtc.Ticks,
              StopTicks=engine.LastParkStopWriteUtc.Ticks,
              Status=engine.GetStatusString()});
            await Task.Delay(250,clock.Token);
          }
        }
        catch(OperationCanceledException){}
      });
      try
      {
        while(connection.IsConnected)
        {
          string? line=await reader.ReadLineAsync();
          if(line==null)break;
          var cmd=JsonSerializer.Deserialize<RotatorWireMessage>(line);
          if(cmd==null)continue;
          bool reply=false;
          switch(cmd.Type)
          {
            case "move":
              if(cmd.Az.HasValue&&cmd.El.HasValue)
                engine.RotateTo(new Bearing(cmd.Az.Value*Math.PI/180,
                  cmd.El.Value*Math.PI/180));
              break;
            case "jog": engine.StartContinuousMove(
              (RotatorContinuousDirection)cmd.Direction);break;
            case "stop":engine.StopRotation();break;
            case "retry":engine.Retry();break;
            case "diagnostics":engine.SetParkDiagnostics(cmd.Flag);break;
            case "lease":
              lease?.Dispose();lease=null;
              lease=await engine.AcquireManagementLeaseAsync();
              reply=lease!=null;break;
            case "release":lease?.Dispose();lease=null;break;
            case "verify":reply=await engine.VerifyPostManagementFeedbackAsync();break;
            case "latch":engine.LatchAfterManagementChange();break;
          }
          if(cmd.Type is "lease" or "verify")
            await Publish(new RotatorWireMessage{
              Type="reply",Id=cmd.Id,Flag=reply});
        }
      }
      finally
      {
        clock.Cancel();
        try{await states.WaitAsync(TimeSpan.FromSeconds(1));}catch(Exception){}
      }
    }
    catch(Exception ex)
    {
      try{Log.Warning(ex,"Rotator engine child failure");}catch{}
    }
    finally
    {
      // SkyRoof vanished or requested shutdown. Never continue moving after
      // control pipe EOF. STOP attempt is issued before child process exit.
      try{native?.StopRotation();}catch{}
      try{lease?.Dispose();}catch{}
      try{native?.Dispose();}catch{}
      Log.CloseAndFlush();
      InChildEngine=false;
    }
  }
}

internal sealed class RotatorWireMessage
{
  public string Type {get;set;}="";
  public long Id {get;set;}
  public bool Flag {get;set;}
  public int Direction {get;set;}
  public double? Az {get;set;}
  public double? El {get;set;}
  public double? Az2 {get;set;}
  public double? El2 {get;set;}
  public double? RequestAz {get;set;}
  public double? RequestEl {get;set;}
  public double? WrittenAz {get;set;}
  public double? WrittenEl {get;set;}
  public double? RateAz {get;set;}
  public double? RateEl {get;set;}
  public long ReadTicks {get;set;}
  public long TargetTicks {get;set;}
  public long StopTicks {get;set;}
  public long RateTicks {get;set;}
  public bool Running {get;set;}
  public bool Ready {get;set;}
  public bool Faulted {get;set;}
  public bool Review {get;set;}
  public string? Status {get;set;}
}
