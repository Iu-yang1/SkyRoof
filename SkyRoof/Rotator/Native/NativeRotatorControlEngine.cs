using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;
using SkyRoof.NativeRotator;

namespace SkyRoof;

internal interface IRotatorEngine : IDisposable
{
  Bearing? RequestedBearing { get; }
  Bearing? LastReadBearing { get; }
  Bearing? LastWrittenBearing { get; }
  DateTime LastSuccessfulBearingReadUtc { get; }
  bool PositionFaulted { get; }
  bool IsRunning { get; }
  event EventHandler? StatusChanged;
  event EventHandler? BearingChanged;
  event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;
  void RotateTo(Bearing bearing);
  void StartContinuousMove(RotatorContinuousDirection direction);
  void StopRotation();
  void Retry();
}

internal sealed class NativeRotatorControlEngine : IRotatorEngine
{
  private readonly RotatorSettings settings;
  private readonly SynchronizationContext ui;
  private readonly object sync = new();
  private readonly AutoResetEvent wake = new(false);
  private readonly ManualResetEvent stop = new(false);
  private readonly ManualResetEventSlim stopCompleted = new(false);
  private readonly Thread worker;
  private IRotatorTransport? transport;
  private Bearing? requested,lastRead,lastWritten;
  private DateTime lastReadUtc = DateTime.MinValue;
  private volatile bool stopping,stopRequested;
  private bool connected,safetyLatched,requiresStopAfterReconnect;
  private int? manualDirection,appliedDirection;
  private byte[]? lastWire;
  private string? error;
  private long commandVersion;
  private readonly Queue<byte> replyBacklog = new();
  private int spidAzResolution = 0, spidElResolution = 0;
  private int consecutiveFeedbackMisses;
  private int stableTargetReads;
  private DateTime stableTargetFirstUtc = DateTime.MinValue;
  private DateTime lastSuccessfulSessionFeedbackUtc = DateTime.MinValue;
  private volatile int ready;
  private long connectionAttemptNumber;
  public event EventHandler? StatusChanged;
  public event EventHandler? BearingChanged;
  public event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;

  public Bearing? RequestedBearing {get {lock(sync)return requested;}}
  public Bearing? LastReadBearing {get {lock(sync)return lastRead;}}
  public Bearing? LastWrittenBearing {get {lock(sync)return lastWritten;}}
  public DateTime LastSuccessfulBearingReadUtc {get {lock(sync)return lastReadUtc;}}
  public bool PositionFaulted {get {lock(sync)return safetyLatched;}}
  public bool IsRunning {get {lock(sync)return connected;}}
  public bool IsReady => Volatile.Read(ref ready) == 1;
  public string GetStatusString()=>PositionFaulted ? "Safety locked" :
    !IsRunning ? "Reconnecting" : IsReady ? "Ready" : "Synchronizing";
  public NativeRotatorControlEngine(RotatorSettings settings)
  {
    this.settings=settings;
    if (!NativeRotatorCodec.Supports(settings.NativeProtocol,settings.NativeTransport))
      throw new ArgumentException("This protocol cannot use selected transport");
    if (settings.NativeProtocol==RotatorProtocolKind.CustomTemplate &&
        string.IsNullOrWhiteSpace(settings.NativePointTemplate))
      throw new ArgumentException("Custom protocol must have a point template");
    ui=SynchronizationContext.Current??new SynchronizationContext();
    worker=new Thread(Run){IsBackground=true,Name="NativeRotatorIO",Priority=ThreadPriority.Normal};
    worker.Start();
  }
  private void Post(Action action)=>ui.Post(_=>{if(!stopping) action();},null);
  private void StateChanged()
  {
    if(StatusChanged!=null)
      Post(()=>StatusChanged?.Invoke(this,EventArgs.Empty));
  }
  // Called by MainForm's one-second retry tick. Only wake a disconnected
  // worker; pacing is enforced by the reconnect loop so this cannot turn
  // into a tight connect/refuse storm.
  public void Retry()
  {
    if(!IsRunning)wake.Set();
  }
  public void RotateTo(Bearing bearing)
  {
    if(!double.IsFinite(bearing.AzDeg)||!double.IsFinite(bearing.ElDeg))return;
    if(bearing.AzDeg<settings.MinAzimuth||bearing.AzDeg>settings.MaxAzimuth ||
       bearing.ElDeg<settings.MinElevation||bearing.ElDeg>settings.MaxElevation)return;
    lock(sync)
    {
      if(stopping || safetyLatched)return;
      // Never send a new target before the first real position observation,
      // or after feedback becomes stale. Open-loop protocols need a separate
      // explicit user opt-in.
      if(!settings.NativeAllowOpenLoopMotion &&
         (!CanFeedback || !HasFreshPositionLocked()))return;
      if(!NativeRotatorCodec.CanStop(settings.NativeProtocol,settings.NativeStopTemplate) &&
         !settings.NativeAllowOpenLoopMotion)return;
      if(appliedDirection.HasValue||manualDirection.HasValue)stopRequested=true;
      requested=bearing;manualDirection=null;
      commandVersion++;
    }
    wake.Set();
  }
  public void StartContinuousMove(RotatorContinuousDirection direction)
  {
    if(!NativeRotatorCodec.CanJog(settings.NativeProtocol))return;
    lock(sync)
    {
      if(stopping || safetyLatched)return;
      if(!settings.NativeAllowOpenLoopMotion && !HasFreshPositionLocked())return;
      int value=(int)direction;
      if(value is not (2 or 4 or 8 or 16))return;
      if(appliedDirection.HasValue && appliedDirection!=value || lastWire!=null ||
          manualDirection.HasValue && manualDirection!=value)stopRequested=true;
      requested=lastWritten=null;manualDirection=value;lastWire=null;
      commandVersion++;
    }
    wake.Set();
  }
  public void StopRotation()
  {
    lock(sync)
    {
      requested=lastWritten=null;
      manualDirection=null;
      lastWire=null;
      stopCompleted.Reset();
      stopRequested=true;
      commandVersion++;
    }
    wake.Set();
  }
  private void SetConnected(bool value)
  {
    bool changed;
    lock(sync){changed=connected!=value;connected=value;}
    if(!value)Volatile.Write(ref ready,0);
    if(changed)StateChanged();
  }
  private byte[] ReadReply(Func<byte[], bool> complete, int timeoutMs)
  {
    // Preserve the unused tail of a serial/TCP read. For example, a device
    // may return two newline-delimited replies in a single OS read; consuming
    // the first must NOT discard the second.
    var pending = new List<byte>(128);
    var buffer = new byte[256];
    long deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 30000);
    while (!stopping && Environment.TickCount64 < deadline)
    {
      if (stopRequested) throw new OperationCanceledException("STOP pending");
      if (replyBacklog.Count == 0)
      {
        int received;
        try { received = transport!.Read(buffer); }
        catch (TimeoutException) { received = 0; }
        if (received == 0)
        {
          if (stopRequested) throw new OperationCanceledException("STOP pending");
          if (stop.WaitOne(10)) throw new OperationCanceledException("Engine disposed");
          continue;
        }
        for (int i = 0; i < received; i++)
          replyBacklog.Enqueue(buffer[i]);
        if (replyBacklog.Count > 4096)
          throw new InvalidDataException("Rotator receive queue exceeded 4096 bytes");
      }

      while (replyBacklog.Count > 0)
      {
        if (stopRequested) throw new OperationCanceledException("STOP pending");
        pending.Add(replyBacklog.Dequeue());
        if (pending.Count > 4096)
          throw new InvalidDataException("Rotator response exceeded 4096 bytes");
        if (complete(pending.ToArray()))
          return pending.ToArray();
      }
    }
    throw new TimeoutException("Native rotator position reply timed out");
  }

  private bool HasFreshPositionLockedThreadSafe()
  {
    lock(sync) return HasFreshPositionLocked();
  }

  private bool HasFreshPositionLocked() =>
    connected && lastReadUtc != DateTime.MinValue &&
    (DateTime.UtcNow-lastReadUtc).TotalMilliseconds <=
      Math.Max(2500,3L*Math.Clamp(settings.NativePollMs,200,30000));

  private byte[]? CommandBytes() => NativeRotatorCodec.Query(settings.NativeProtocol,settings.NativeQueryTemplate);
  private bool CanFeedback => settings.NativeProtocol==RotatorProtocolKind.CustomTemplate
    ? !string.IsNullOrWhiteSpace(settings.NativeResponseTemplate) &&
      (!string.IsNullOrWhiteSpace(settings.NativeQueryTemplate)||settings.NativeTransport==RotatorTransportKind.Udp)
    : settings.NativeProtocol==RotatorProtocolKind.PstRotator ||
      NativeRotatorCodec.CanQuery(settings.NativeProtocol);
  private void SendStop()
  {
    byte[]? frame=NativeRotatorCodec.Stop(settings.NativeProtocol,settings.NativeStopTemplate);
    if(frame==null)
    {
      lock(sync){safetyLatched=true;error="STOP not supported by rotator protocol";}
      stopCompleted.Set();StateChanged();return;
    }
    transport!.Write(frame);
    if(settings.NativeProtocol==RotatorProtocolKind.Rotctld)
    {
      // Hamlib STOP is the only wire protocol here with an actual ACK.
      // Always consume it before the next p/P command.
      var ack=ReadStopAck(settings.NativeReadTimeout);
      if(!Regex.IsMatch(Encoding.ASCII.GetString(ack),@"(?m)^RPRT 0\s*$"))
        throw new IOException("rotctld rejected STOP");
    }
    lock(sync)
    {
      appliedDirection=null;lastWire=null;lastWritten=null;
      requiresStopAfterReconnect=false;
      stableTargetReads=0;
      stableTargetFirstUtc=DateTime.MinValue;
    }
    stopCompleted.Set();
    // Serial/TCP bridge writes do NOT guarantee a physical motor STOP ACK.
  }
  private byte[] ReadStopAck(int timeout)
  {
    // Stop is already sent. Consume one full line without considering the
    // pending STOP flag, which is cleared by the scheduler before SendStop.
    return ReadReply(bytes=>bytes.Length>0 &&
      bytes[^1]==(byte)'\n' &&
      Regex.IsMatch(Encoding.ASCII.GetString(bytes),@"RPRT\s+-?\d+"),timeout);
  }
  private RotatorPosition Query()
  {
    var protocol=settings.NativeProtocol;
    if(protocol is RotatorProtocolKind.EasyCommII or RotatorProtocolKind.EasyCommIII)
    {
      transport!.Write(Encoding.ASCII.GetBytes("AZ\n"));
      var a=ReadReply(bytes=>bytes.Length>0 &&
        (bytes[^1]==(byte)'\n' || bytes[^1]==(byte)'\r') &&
        NativeRotatorCodec.TryParseAxis(bytes,"AZ",out _),settings.NativeReadTimeout);
      if(!NativeRotatorCodec.TryParseAxis(a,"AZ",out double az))throw new InvalidDataException("Bad EasyComm azimuth");
      transport.Write(Encoding.ASCII.GetBytes("EL\n"));
      var e=ReadReply(bytes=>bytes.Length>0 &&
        (bytes[^1]==(byte)'\n' || bytes[^1]==(byte)'\r') &&
        NativeRotatorCodec.TryParseAxis(bytes,"EL",out _),settings.NativeReadTimeout);
      if(!NativeRotatorCodec.TryParseAxis(e,"EL",out double el))throw new InvalidDataException("Bad EasyComm elevation");
      return new RotatorPosition(az,el);
    }
    byte[]? query=CommandBytes();
    if(query!=null)transport!.Write(query);
    var b=ReadReply(bytes=>
      (protocol==RotatorProtocolKind.SpidRot2Prog ||
       settings.NativeTransport==RotatorTransportKind.Udp ||
       protocol==RotatorProtocolKind.Oz9AarUrc && bytes.Length>0 && bytes[^1]==(byte)'}' ||
       bytes.Length>0 && (bytes[^1]==(byte)'\n'||bytes[^1]==(byte)'\r')) &&
      NativeRotatorCodec.TryParse(protocol,bytes,out _,settings.NativeResponseTemplate),
      settings.NativeReadTimeout);
    if(!NativeRotatorCodec.TryParse(protocol,b,out var position,settings.NativeResponseTemplate))
      throw new InvalidDataException("Invalid rotator position");
    return position;
  }
  private void PublishPosition(RotatorPosition p)
  {
    if(!NativeRotatorCodec.Valid(p))throw new InvalidDataException("Out-of-range rotor position");
    Bearing current=new(p.Azimuth*Math.PI/180,p.Elevation*Math.PI/180);
    Bearing? previous; DateTime oldUtc;
    bool fault=false;
    lock(sync)
    {
      previous=lastRead;oldUtc=lastReadUtc;
      double azdiff=previous==null?0:Math.Abs(previous.AzDeg-current.AzDeg);
      if(previous!=null && previous.AzDeg is >=0 and <=360 && current.AzDeg is >=0 and <=360)
        azdiff=Math.Min(azdiff,360-azdiff);
      double eldiff=previous==null?0:Math.Abs(previous.ElDeg-current.ElDeg);
      if(previous!=null && oldUtc!=DateTime.MinValue &&
         (DateTime.UtcNow-oldUtc).TotalSeconds<=2 &&
         (azdiff>settings.PositionJumpLimitDegrees||eldiff>settings.PositionJumpLimitDegrees))
      {
        fault=safetyLatched=true;requested=lastWritten=null;
        manualDirection=null;stopRequested=true;lastWire=null;
        error="Position feedback jump: STOP sent or pending";
      }
      lastRead=current;lastReadUtc=DateTime.UtcNow;
      // STOP after link loss is necessary only if a previously issued target
      // may still be in motion. Two fresh measurements at the target, over
      // at least 200ms, confirm the target has settled. Continuous manual jog
      // never clears the interlock this way: it always requires STOP.
      if(!fault && requested!=null && !manualDirection.HasValue &&
         !appliedDirection.HasValue && lastWire!=null &&
         Math.Abs(current.AzDeg-requested.AzDeg)<=1.5 &&
         Math.Abs(current.ElDeg-requested.ElDeg)<=1.5)
      {
        if(stableTargetReads==0)stableTargetFirstUtc=lastReadUtc;
        stableTargetReads++;
        if(stableTargetReads>=2 &&
           (lastReadUtc-stableTargetFirstUtc).TotalMilliseconds>=200)
          requiresStopAfterReconnect=false;
      }
      else
      {
        stableTargetReads=0;
        stableTargetFirstUtc=DateTime.MinValue;
      }
    }
    // Unlike the legacy engine, the first native implementation posted an
    // event after EVERY position poll, even when the bearings were identical
    // and no listener existed. Avoid flooding WinForms/xUnit contexts with
    // thousands of redundant marshaled callbacks during idle tracking.
    if((previous==null || previous!=current) && BearingChanged!=null)
      Post(()=>BearingChanged?.Invoke(this,EventArgs.Empty));
    if(fault && previous!=null)
    {
      // We're already on the dedicated I/O thread: attempt STOP before
      // notifying WinForms, so the safety dialog cannot delay the write.
      // The hardware has no physical STOP acknowledgement; always report
      // its outcome as unconfirmed even after a successful serial write.
      try
      {
        stopRequested = false;
        SendStop();
      }
      catch(Exception ex)
      {
        Log.Error(ex,"Native rotator emergency STOP could not be confirmed");
        lock(sync){stopRequested=true;}
      }
      Post(()=>PositionFaultDetected?.Invoke(this,new RotatorPositionFaultEventArgs(
        previous,current,Math.Abs(previous.AzDeg-current.AzDeg),
        Math.Abs(previous.ElDeg-current.ElDeg),false)));
      wake.Set();
    }
  }
  private void Run()
  {
    int retryFailures=0;
    bool hadConnection=false;
    while(!stopping)
    {
      try
      {
        using var device=NativeTransportFactory.Create(settings);
        transport=device;
        long attempt=Interlocked.Increment(ref connectionAttemptNumber);
        var connectWatch=System.Diagnostics.Stopwatch.StartNew();
        Log.Information("Native rotator: connecting attempt {Attempt} via {Transport} to {Host}:{Port}, protocol {Protocol}",
          attempt,settings.NativeTransport,
          settings.NativeTransport==RotatorTransportKind.Serial ? settings.NativeSerialPort : settings.Host,
          settings.NativeTransport==RotatorTransportKind.Serial ? 0 : settings.Port,
          settings.NativeProtocol);
        device.Open();
        Log.Information("Native rotator transport established after {ElapsedMs} ms; synchronizing controller feedback",
          connectWatch.ElapsedMilliseconds);
        replyBacklog.Clear();
        consecutiveFeedbackMisses=0;
        stableTargetReads=0;
        stableTargetFirstUtc=DateTime.MinValue;
        Volatile.Write(ref ready,CanFeedback ? 0 : 1);
        spidAzResolution = spidElResolution = 0;
        lock(sync)
        {
          // Invalidate old connection's feedback; it must not authorize a
          // fresh motion command after a reconnect.
          lastRead = null;
          lastReadUtc = DateTime.MinValue;
          lastSuccessfulSessionFeedbackUtc=DateTime.MinValue;
        }
        SetConnected(true);
        if(hadConnection)
        {
          lock(sync)
          {
            // Do not lock the controls merely because an idle position
            // query timed out. Fail-closed only if a motion command could
            // still be active on the physical controller.
            bool potentiallyMoving=requiresStopAfterReconnect ||
              appliedDirection.HasValue;
            requested=lastWritten=null;manualDirection=null;appliedDirection=null;lastWire=null;
            if(potentiallyMoving)
            {
              safetyLatched=true;
              stopRequested=true;
              requiresStopAfterReconnect=true;
            }
          }
        }
        hadConnection=true;
        if(!CanFeedback)retryFailures=0;
        long nextQuery=0;
        while(!stopping && device.Connected)
        {
          try
          {
          bool stopNow;int? jog;Bearing? target;long selectedVersion;
          lock(sync)
          {
            stopNow=stopRequested;stopRequested=false;
            jog=manualDirection;target=requested;
            selectedVersion=commandVersion;
          }
          if(stopNow)
          {
            SendStop();
            continue;
          }
          if(jog.HasValue && !PositionFaulted)
          {
            bool shouldSend;
            lock(sync){shouldSend=appliedDirection!=jog;}
            if(shouldSend)
            {
              if(appliedDirection.HasValue)SendStop();
              RotatorDirection direction=jog.Value switch
              {2=>RotatorDirection.Up,4=>RotatorDirection.Down,8=>RotatorDirection.Left,16=>RotatorDirection.Right,
                _=>throw new InvalidDataException("Invalid motor direction")};
              if(stopRequested || Interlocked.Read(ref commandVersion)!=selectedVersion ||
                !settings.NativeAllowOpenLoopMotion && !HasFreshPositionLockedThreadSafe())
                continue; // released, switched or feedback went stale
              // A failed serial write may still have transmitted enough
              // bytes to start a motor. Latch possible motion BEFORE I/O.
              lock(sync)requiresStopAfterReconnect=true;
              device.Write(NativeRotatorCodec.Jog(direction));
              lock(sync)appliedDirection=jog;
            }
          }
          else if(target!=null && !PositionFaulted)
          {
            var p=new RotatorPosition(target.AzDeg,target.ElDeg);
            byte[] frame;
            if(settings.NativeProtocol==RotatorProtocolKind.SpidRot2Prog)
            {
              // Read SPID resolution once per connection, not on every loop
              // iteration when an unchanged target is pending.
              if(spidAzResolution == 0 || spidElResolution == 0)
              {
                var reply=QueryRawSpid();
                if(!NativeRotatorCodec.TryParseSpid(reply,out _,out spidAzResolution,out spidElResolution))
                  throw new InvalidDataException("Missing SPID axis resolution");
              }
              frame=NativeRotatorCodec.Point(settings.NativeProtocol,p,
                settings.NativePointTemplate,spidAzResolution,spidElResolution);
            }
            else frame=NativeRotatorCodec.Point(settings.NativeProtocol,p,settings.NativePointTemplate);
            bool same;
            lock(sync){same=lastWire!=null&&lastWire.SequenceEqual(frame);}
            if(!same)
            {
              if(stopRequested || Interlocked.Read(ref commandVersion)!=selectedVersion ||
                !settings.NativeAllowOpenLoopMotion && !HasFreshPositionLockedThreadSafe())
                continue; // STOP superseded target or live feedback expired
              lock(sync)requiresStopAfterReconnect=true;
              device.Write(frame);
              if(settings.NativeProtocol==RotatorProtocolKind.Rotctld)
              {
                var ack=ReadReply(b=>Regex.IsMatch(Encoding.ASCII.GetString(b),@"RPRT\s+-?\d+"),
                   settings.NativeReadTimeout);
                if(!Encoding.ASCII.GetString(ack).Contains("RPRT 0"))
                  throw new IOException("rotctld rejected position");
              }
              lock(sync){lastWritten=target;lastWire=frame;}
            }
          }
          long now=Environment.TickCount64;
          if(CanFeedback && now>=nextQuery && !stopRequested)
          {
            nextQuery=now+Math.Clamp(settings.NativePollMs,200,30000);
            try
            {
              PublishPosition(Query());
              if(consecutiveFeedbackMisses>0)
                Log.Information("Native rotator: feedback recovered after {Misses} missed replies",
                  consecutiveFeedbackMisses);
              consecutiveFeedbackMisses=0;
              lastSuccessfulSessionFeedbackUtc=DateTime.UtcNow;
              if(Interlocked.Exchange(ref ready,1)==0)StateChanged();
              retryFailures=0;
            }
            catch(OperationCanceledException) when(stopRequested){continue;}
            catch(TimeoutException ex) when(settings.NativeProtocol==RotatorProtocolKind.PstRotator ||
              settings.NativeProtocol==RotatorProtocolKind.CustomTemplate &&
              settings.NativeTransport==RotatorTransportKind.Udp)
            {
              // PST is passive UDP: no packet does not indicate a broken connection.
              continue;
            }
            catch(TimeoutException ex) when(settings.NativeTransport==RotatorTransportKind.Tcp)
            {
              ++consecutiveFeedbackMisses;
              Log.Warning("Native rotator: TCP feedback timeout {Misses}/3 from {Host}:{Port}; last valid feedback {AgeMs} ms ago. Keeping socket for recovery.",
                consecutiveFeedbackMisses,settings.Host,settings.Port,
                lastSuccessfulSessionFeedbackUtc==DateTime.MinValue ? -1 :
                  (long)(DateTime.UtcNow-lastSuccessfulSessionFeedbackUtc).TotalMilliseconds);
              if(consecutiveFeedbackMisses>=3)
                throw new TimeoutException("Native TCP rotator: 3 consecutive protocol feedback timeouts",ex);
              continue;
            }
          }
          int remaining=(int)Math.Clamp(nextQuery-Environment.TickCount64,20,1000);
          if(!CanFeedback)remaining=200;
          wake.WaitOne(remaining);
          }
          catch(OperationCanceledException) when(stopRequested && !stopping)
          {
            // A new emergency STOP interrupted an in-progress SPID query or
            // rotctld ACK. Stay on this connection and service STOP next.
            // Disconnecting here could leave a continuous motor moving.
            continue;
          }
        }
      }
      catch(Exception ex)
      {
        if(!stopping)
        {
          Log.Warning(ex,"Native rotator I/O failure ({Transport}, {Protocol}) after attempt {Attempt}. Reconnecting.",
            settings.NativeTransport,settings.NativeProtocol,Interlocked.Read(ref connectionAttemptNumber));
          lock(sync)
          {
            error=ex.Message;
            if(requiresStopAfterReconnect || appliedDirection.HasValue || lastWire!=null)
            {
              safetyLatched=true;
              stopRequested=true;
            }
            requested=lastWritten=null;manualDirection=null;appliedDirection=null;lastWire=null;
          }
        }
      }
      finally
      {
        SetConnected(false);
        try{transport?.Abort();}catch{}
        transport=null;
      }
      if(!stopping)
      {
        int delay=NativeRotatorReconnectPolicy.GetDelayMilliseconds(
          retryFailures++,settings.ReconnectDelay);
        Log.Information("Native rotator: reconnect backoff {DelayMs} ms (failure streak {Failures})",
          delay,retryFailures);
        // Retry() is called once per second by MainForm. It may wake the
        // reconnect gate, but never permit attempts more frequently than
        // this minimum interval after the last failure.
        int waitResult=WaitHandle.WaitAny(new WaitHandle[]{stop,wake},delay);
        if(waitResult==0)break;
        if(waitResult==1 && !stop.WaitOne(0))
        {
          int minimumDelay=Math.Min(750,delay);
          if(stop.WaitOne(minimumDelay))break;
        }
      }
    }
  }
  private byte[] QueryRawSpid()
  {
    transport!.Write(NativeRotatorCodec.Query(RotatorProtocolKind.SpidRot2Prog)!);
    return ReadReply(b=>NativeRotatorCodec.TryParseSpid(b,out _,out _,out _),settings.NativeReadTimeout);
  }
  public void Dispose()
  {
    if(stopping)return;
    StopRotation();
    // The background I/O thread handles STOP; waiting is bounded by active read.
    wake.Set();
    // Do not hold up WinForms when no device is connected. When connected,
    // allow a bounded interval for a STOP write before aborting the handle.
    if(IsRunning)
      stopCompleted.Wait(Math.Clamp(settings.NativeReadTimeout+100,200,1000));
    stopping=true;stop.Set();
    try{transport?.Abort();}catch{}
    wake.Set();
    bool joined = Thread.CurrentThread != worker && worker.Join(2500);
    if(!joined)
      Log.Warning("Native rotator worker shutdown timed out; inspect COM driver");
    // A wedged vendor serial driver may not release a blocked worker. Leave
    // its wait handles intact until process exit rather than causing an
    // ObjectDisposedException from a still-running worker.
    if(joined){wake.Dispose();stop.Dispose();stopCompleted.Dispose();}
  }
}
