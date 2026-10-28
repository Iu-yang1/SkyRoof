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

internal sealed class NativeRotatorControlEngine : INativeRotatorEngine
{
  private readonly RotatorSettings settings;
  private readonly bool plannerAuthorizedAtEngineStart;
  private readonly bool plannerRequiresIndependentCalibration;
  private readonly Rc3040sMotionProfile plannerProfileAtEngineStart;
  private readonly Rc3040sMotionPlanner.Limits plannerLimitsAtEngineStart;
  private readonly SynchronizationContext ui;
  private readonly object sync = new();
  private readonly AutoResetEvent wake = new(false);
  private readonly ManualResetEvent stop = new(false);
  private readonly ManualResetEventSlim stopCompleted = new(false);
  private readonly Thread worker;
  private IRotatorTransport? transport;
  private Bearing? requested,lastRead,lastWritten;
  private DateTime lastReadUtc = DateTime.MinValue;
  private DateTime lastTargetWriteUtc = DateTime.MinValue;
  private DateTime lastStopWriteUtc = DateTime.MinValue;
  private bool parkDiagnosticsActive;
  private volatile bool stopping,stopRequested;
  private bool connected,safetyLatched,requiresStopAfterReconnect,managementHeld,managementReviewRequired;
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
  private readonly Rc3040sFeedbackEstimator rateEstimator=new();
  private Rc3040sFeedbackEstimator.Observation? latestRate;
  public Rc3040sFeedbackEstimator.Observation? LatestObservedRate
  {get{lock(sync)return latestRate;}}
  public event EventHandler? StatusChanged;
  public event EventHandler? BearingChanged;
  public event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;

  public Bearing? RequestedBearing {get {lock(sync)return requested;}}
  public Bearing? LastReadBearing {get {lock(sync)return lastRead;}}
  public Bearing? LastWrittenBearing {get {lock(sync)return lastWritten;}}
  public DateTime LastSuccessfulBearingReadUtc {get {lock(sync)return lastReadUtc;}}
  public DateTime LastParkTargetWriteUtc {get {lock(sync)return lastTargetWriteUtc;}}
  public DateTime LastParkStopWriteUtc {get {lock(sync)return lastStopWriteUtc;}}
  /// <summary>
  /// True only after two separate, sufficiently spaced protocol readbacks
  /// at the current commanded target have cleared the reconnect STOP
  /// interlock. Internal diagnostic for deterministic recovery tests.
  /// This is controller-reported settling, not physical shaft verification.
  /// </summary>
  internal bool TargetSettledForReconnect
  {
    get
    {
      lock(sync)return requested!=null && stableTargetReads>=2 &&
        stableTargetFirstUtc!=DateTime.MinValue &&
        (lastReadUtc-stableTargetFirstUtc).TotalMilliseconds>=200 &&
        !requiresStopAfterReconnect;
    }
  }
  public void SetParkDiagnostics(bool active)
  {
    lock(sync)parkDiagnosticsActive=active;
  }
  public bool PositionFaulted {get {lock(sync)return safetyLatched;}}
  public bool ManagementReviewRequired {get {lock(sync)return managementReviewRequired;}}
  public bool IsRunning {get {lock(sync)return connected;}}
  public bool IsReady => Volatile.Read(ref ready) == 1;
  public bool HasLiveConnection => IsRunning && IsReady;
  public string GetStatusString() =>
    !IsRunning ? "Reconnecting" :
    ManagementReviewRequired ? "Management safety lock; inspect and reapply settings" :
    PositionFaulted ? "Position safety fault; STOP requested" :
    !IsReady && Volatile.Read(ref consecutiveFeedbackMisses)>0
      ? $"Position feedback delayed ({Volatile.Read(ref consecutiveFeedbackMisses)} missed); motion paused"
      : !IsReady ? "Synchronizing" :
    plannerRequiresIndependentCalibration
      ? "Motion planning disabled: verify shaft AZ/EL, STOP and integer-degree limits"
      : "Ready";
  public NativeRotatorControlEngine(RotatorSettings settings)
  {
    this.settings=settings;
    // Motion settings are sampled when the motor worker starts; changing
    // UI preferences never enables a new profile mid-movement. An operator
    // must stop and re-apply rotator settings to activate a new calibration.
    plannerProfileAtEngineStart=settings.Rc3040sMotionMode;
    plannerLimitsAtEngineStart=new Rc3040sMotionPlanner.Limits(
      settings.Rc3040sPlanAzSpeed,settings.Rc3040sPlanElSpeed,
      settings.Rc3040sPlanAcceleration,settings.Rc3040sPlanJerk,
      settings.MinAzimuth,settings.MaxAzimuth,
      settings.MinElevation,settings.MaxElevation);
    plannerRequiresIndependentCalibration=
      settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan &&
      settings.Rc3040sPlannerEnabled &&
      plannerProfileAtEngineStart!=Rc3040sMotionProfile.Direct &&
      (!settings.Rc3040sMotionHardwareValidated ||
       !settings.Rc3040sIndependentAngleVerified ||
       !Rc3040sPlannerFeedbackGuard.ValidQuantizedLimits(plannerLimitsAtEngineStart));
    plannerAuthorizedAtEngineStart=
      settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan &&
      settings.Rc3040sPlannerEnabled &&
      settings.Rc3040sMotionHardwareValidated &&
      settings.Rc3040sIndependentAngleVerified &&
      plannerProfileAtEngineStart!=Rc3040sMotionProfile.Direct &&
      Rc3040sPlannerFeedbackGuard.ValidQuantizedLimits(plannerLimitsAtEngineStart);
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
      if(stopping || safetyLatched || managementHeld ||
         plannerRequiresIndependentCalibration)return;
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
      if(stopping || safetyLatched || managementHeld)return;
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
  /// <summary>
  /// Prevent *all new motor positioning and manual jogs* during a one-shot
  /// WS/60030 management write or restart. Only STOP and feedback polling
  /// remain permitted on TCP/60020. This is a motor command serialization
  /// lease, not evidence of a physically stopped shaft.
  /// </summary>
  public async Task<IDisposable?> AcquireManagementLeaseAsync(
    CancellationToken cancellationToken=default)
  {
    lock(sync)
    {
      if(managementHeld || stopping || safetyLatched ||
         !HasFreshPositionLocked())
        return null;
      managementHeld=true;
      requested=lastWritten=null;manualDirection=null;lastWire=null;
      stopCompleted.Reset();stopRequested=true;commandVersion++;
    }
    wake.Set();
    try
    {
      if(!await WaitForStopTransmissionAsync(2500,cancellationToken)
        .ConfigureAwait(false))
      {
        ReleaseManagementLease();
        return null;
      }
      return new ManagementLease(this);
    }
    catch
    {
      ReleaseManagementLease();
      throw;
    }
  }

  public void LatchAfterManagementChange()
  {
    // Ambiguous write results and unavailable motor feedback remain locked.
    // Distinguish this state from an actual network disconnection.
    lock(sync)
    {
      managementReviewRequired=true;
      safetyLatched=true;requiresStopAfterReconnect=true;
      requested=lastWritten=null;manualDirection=null;lastWire=null;
      stopRequested=true;commandVersion++;
      error="RC-3040S management change: inspect physical position and reapply rotator settings";
    }
    wake.Set();StateChanged();
  }

  /// <summary>
  /// Following a verified speed readback on WebSocket/60030, wait for a new
  /// healthy position sample from the independent motor TCP/60020 worker.
  /// Queued target and jog commands remain blocked until lease release.
  /// </summary>
  public async Task<bool> VerifyPostManagementFeedbackAsync(
    CancellationToken cancellationToken=default)
  {
    DateTime checkpoint=DateTime.UtcNow;
    long deadline=Environment.TickCount64+4000;
    while(Environment.TickCount64<deadline)
    {
      cancellationToken.ThrowIfCancellationRequested();
      lock(sync)
      {
        if(stopping || !managementHeld || safetyLatched ||
           managementReviewRequired)return false;
        if(HasFreshPositionLocked() && lastReadUtc>checkpoint &&
           !requiresStopAfterReconnect && !stopRequested)
          return true;
      }
      wake.Set();
      await Task.Delay(100,cancellationToken).ConfigureAwait(false);
    }
    return false;
  }

  private void ReleaseManagementLease()
  {
    lock(sync)managementHeld=false;
    StateChanged();
  }

  private sealed class ManagementLease : IDisposable
  {
    private NativeRotatorControlEngine? owner;
    internal ManagementLease(NativeRotatorControlEngine engine)=>owner=engine;
    public void Dispose()=>Interlocked.Exchange(ref owner,null)?.ReleaseManagementLease();
  }

  internal Task<bool> WaitForStopTransmissionAsync(int timeoutMilliseconds,
      CancellationToken cancellationToken=default)
  {
    // The manual reset event is set when the GS-232 S frame is transmitted.
    // It is NOT a motor brake/encoder acknowledgment.
    return Task.Run(() => stopCompleted.Wait(
      Math.Clamp(timeoutMilliseconds,100,3000),cancellationToken),cancellationToken);
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
    connected && Volatile.Read(ref ready)==1 &&
    Volatile.Read(ref consecutiveFeedbackMisses)==0 &&
    lastReadUtc != DateTime.MinValue &&
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
    lock(sync)
    {
      lastStopWriteUtc=DateTime.UtcNow;
      if(parkDiagnosticsActive && settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan)
        Log.Information("RC-3040S PARK TX STOP S; transmittedUtc={SentUtc:O}; STOP delivery is not a motor brake ACK",
          lastStopWriteUtc);
    }
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
    bool logPark;
    lock(sync)logPark=parkDiagnosticsActive &&
      protocol==RotatorProtocolKind.Rc3040sLan;
    if(logPark)
    {
      string raw=Encoding.ASCII.GetString(b);
      raw=raw.Replace("\r","\\r").Replace("\n","\\n");
      if(raw.Length>160)raw=raw[..160];
      Log.Information("RC-3040S PARK RX raw C2={Raw}; reported AZ={Az:F2} EL={El:F2}; raw telemetry is not independent physical zero",
        raw,position.Azimuth,position.Elevation);
    }
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
      if(settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan)
      {
        if(rateEstimator.Observe(p,lastReadUtc,out var observation))
          latestRate=observation;
        else latestRate=null;
      }
      // STOP after link loss is necessary only if a previously issued target
      // may still be in motion. Two fresh measurements at the target, over
      // at least 200ms, confirm the target has settled. Continuous manual jog
      // never clears the interlock this way: it always requires STOP.
      if(!fault && requested!=null && !manualDirection.HasValue &&
         !appliedDirection.HasValue && lastWire!=null &&
         Math.Abs(current.AzDeg-requested.AzDeg)<=1.5 &&
         Math.Abs(current.ElDeg-requested.ElDeg)<=1.5 &&
         // A changing Track target can coincide with old C2 while the
         // controller still executes its previous W; never treat that as
         // confirmed STOP. Compare with the actual last wire endpoint.
         Rc3040sPlannerFeedbackGuard.IsLastWireAtReportedPosition(
           p,
           lastWritten==null ? null :
             new RotatorPosition(lastWritten.AzDeg,lastWritten.ElDeg),
           plannerAuthorizedAtEngineStart))
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
    var motionPlanner=new Rc3040sMotionPlanner();
    var plannerFeedback=new Rc3040sPlannerFeedbackGuard();
    long lastPlanTick=0,lastPlannerCommandTick=0;
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
        motionPlanner.Reset();
        plannerFeedback.Reset();
        lastPlanTick=lastPlannerCommandTick=0;
        Volatile.Write(ref consecutiveFeedbackMisses,0);
        stableTargetReads=0;
        stableTargetFirstUtc=DateTime.MinValue;
        Volatile.Write(ref ready,CanFeedback ? 0 : 1);
        spidAzResolution = spidElResolution = 0;
        lock(sync)
        {
          // Invalidate old connection's feedback and wire timestamps.
          lastTargetWriteUtc=lastStopWriteUtc=DateTime.MinValue;
          lastRead = null;
          lastReadUtc = DateTime.MinValue;
          lastSuccessfulSessionFeedbackUtc=DateTime.MinValue;
          rateEstimator.Reset();latestRate=null;
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
        var sessionConnectedUtc=DateTime.UtcNow;
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
            motionPlanner.Reset();plannerFeedback.Reset();lastPlanTick=0;
            SendStop();
            continue;
          }

          if(jog.HasValue && !PositionFaulted)
          {
            motionPlanner.Reset();plannerFeedback.Reset();lastPlanTick=0;
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
              device.Write(NativeRotatorCodec.Jog(direction,settings.NativeProtocol));
              lock(sync)appliedDirection=jog;
            }
          }
          else if(target!=null && !PositionFaulted)
          {
            var p=new RotatorPosition(target.AzDeg,target.ElDeg);
            // Motion authorization/profile/limits are immutable for this
            // worker lifetime. Reading mutable UI settings here used to
            // silently switch a live planned route to an unrestricted
            // Direct W command if a checkbox changed mid-movement.
            bool planning=plannerAuthorizedAtEngineStart;
            bool plannerBlocked=false;
            if(planning)
            {
              Bearing? actual;DateTime readUtc;bool fresh;
              lock(sync)
              {
                actual=lastRead;readUtc=lastReadUtc;
                fresh=HasFreshPositionLocked();
              }
              if(!fresh || actual==null ||
                 readUtc==DateTime.MinValue ||
                 (DateTime.UtcNow-readUtc).TotalSeconds>2.5)
                plannerBlocked=true;
              else
              {
                var observed=new RotatorPosition(
                  actual.AzDeg,actual.ElDeg);
                var destination=new RotatorPosition(
                  target.AzDeg,target.ElDeg);
                bool stalled;
                // Process each valid C2 frame only ONCE. Previously the
                // planner advanced on every 20-450 ms scheduling cycle
                // even when feedback was still from the SAME poll.
                if(!plannerFeedback.TryConsumeFreshFeedback(
                    readUtc,observed,destination,out stalled))
                  plannerBlocked=true;
                else if(stalled)
                {
                  lock(sync)
                  {
                    safetyLatched=true;
                    requiresStopAfterReconnect=true;
                    stopRequested=true;
                    commandVersion++;
                    requested=null;
                    manualDirection=null;
                    lastWire=null;
                    error="RC-3040S planned motion: no measured position progress; STOP requested";
                  }
                  Log.Error("RC-3040S planned motion stalled: C2 AZ={Az:F1} EL={El:F1}; requested AZ={TargetAz:F1} EL={TargetEl:F1}. STOP requested, controller-reported angle is not independent physical shaft evidence.",
                    observed.Azimuth,observed.Elevation,
                    destination.Azimuth,destination.Elevation);
                  plannerBlocked=true;
                  wake.Set();
                  StateChanged();
                }
                else if(plannerFeedback.HasInFlightSetpoint)
                {
                  // C2 may progress toward a previous W but that is NOT
                  // permission to retarget a firmware-driven stepper.
                  // Preserve the in-flight waypoint until two C2 samples
                  // confirm its endpoint; the no-progress STOP remains live.
                  plannerBlocked=true;
                }
                else
                {
                  long tick=Environment.TickCount64;
                  double dt=lastPlanTick==0 ? 0.20 :
                    Math.Clamp((tick-lastPlanTick)/1000.0,0.02,0.5);
                  lastPlanTick=tick;
                  var next=motionPlanner.Step(
                    observed.Azimuth,observed.Elevation,
                    destination.Azimuth,destination.Elevation,
                    dt,plannerProfileAtEngineStart,plannerLimitsAtEngineStart);
                  p=plannerFeedback.BoundSetpoint(observed,
                    new RotatorPosition(next.Azimuth,next.Elevation),
                    plannerLimitsAtEngineStart);
                }
              }
            }
            else if(lastPlanTick!=0)
            {
              motionPlanner.Reset();lastPlanTick=0;
            }
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
            if(planning && (plannerBlocked ||
               lastPlannerCommandTick!=0 &&
               Environment.TickCount64-lastPlannerCommandTick<450))
              same=true;
            if(!same)
            {
              if(stopRequested || Interlocked.Read(ref commandVersion)!=selectedVersion ||
                !settings.NativeAllowOpenLoopMotion && !HasFreshPositionLockedThreadSafe())
                continue; // STOP superseded target or live feedback expired
              lock(sync)requiresStopAfterReconnect=true;
              device.Write(frame);
              lock(sync)
              {
                lastTargetWriteUtc=DateTime.UtcNow;
                if(parkDiagnosticsActive && settings.NativeProtocol==RotatorProtocolKind.Rc3040sLan)
                  Log.Information("RC-3040S PARK TX W target AZ={Az:F1} EL={El:F1}; transmittedUtc={Utc:O}",
                    p.Azimuth,p.Elevation,lastTargetWriteUtc);
              }
              if(settings.NativeProtocol==RotatorProtocolKind.Rotctld)
              {
                var ack=ReadReply(b=>Regex.IsMatch(Encoding.ASCII.GetString(b),@"RPRT\s+-?\d+"),
                   settings.NativeReadTimeout);
                if(!Encoding.ASCII.GetString(ack).Contains("RPRT 0"))
                  throw new IOException("rotctld rejected position");
              }
              if(planning)
              {
                lastPlannerCommandTick=Environment.TickCount64;
                Bearing? reported;
                lock(sync)reported=lastRead;
                if(reported!=null)
                {
                  var observed=new RotatorPosition(
                    reported.AzDeg,reported.ElDeg);
                  plannerFeedback.MarkTransmitted(
                    DateTime.UtcNow,observed,p);
                  Log.Information("RC-3040S planned W endpoint AZ={CommandAz:F0} EL={CommandEl:F0}, last C2 AZ={FeedbackAz:F1} EL={FeedbackEl:F1}, profile={Profile}; endpoint is not measured shaft position",
                    p.Azimuth,p.Elevation,observed.Azimuth,
                    observed.Elevation,plannerProfileAtEngineStart);
                }
              }
              lock(sync)
              {
                lastWritten=planning ?
                  new Bearing(p.Azimuth*Math.PI/180,p.Elevation*Math.PI/180):target;
                lastWire=frame;
              }
            }
          }
          long now=Environment.TickCount64;
          if(CanFeedback && now>=nextQuery && !stopRequested)
          {
            nextQuery=now+Math.Clamp(settings.NativePollMs,200,30000);
            try
            {
              PublishPosition(Query());
              int previousMisses=Interlocked.Exchange(ref consecutiveFeedbackMisses,0);
              if(previousMisses>0)
                Log.Information("Native rotator: position feedback recovered after {Misses} missed replies without replacing connection; {Transport} {Host}:{Port}",
                  previousMisses,settings.NativeTransport,settings.Host,settings.Port);
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
            catch(TimeoutException ex) when(settings.NativeTransport is RotatorTransportKind.Tcp or RotatorTransportKind.Serial)
            {
              // A TX/RFI burst may suppress several controller replies
              // without actually closing TCP or detaching the COM device.
              // Keep the transport while motion is interlocked, then
              // reconnect only after a sustained period of missing feedback.
              int misses=Interlocked.Increment(ref consecutiveFeedbackMisses);
              if(Interlocked.Exchange(ref ready,0)!=0)StateChanged();
              DateTime nowUtc=DateTime.UtcNow;
              bool hadFeedback=lastSuccessfulSessionFeedbackUtc!=DateTime.MinValue;
              long ageMs=(long)(nowUtc-(hadFeedback ?
                lastSuccessfulSessionFeedbackUtc : sessionConnectedUtc)).TotalMilliseconds;

              bool needStop=false;
              lock(sync)
              {
                bool potentiallyMoving=requiresStopAfterReconnect ||
                  appliedDirection.HasValue;
                if(!safetyLatched &&
                   NativeRotatorFeedbackPolicy.MustStopPossibleMotion(
                     misses,ageMs,settings.NativePollMs,potentiallyMoving))
                {
                  // A moving controller with stale feedback cannot be
                  // guaranteed safe. STOP takes priority over every new
                  // target; do not silently resume after RF recovers.
                  safetyLatched=true;
                  stopRequested=true;
                  requested=null;
                  manualDirection=null;
                  lastWire=null;
                  commandVersion++;
                  needStop=true;
                }
              }
              if(needStop)
              {
                Log.Error("Rotator feedback lost while movement may be active; STOP requested and motion latched. Physical STOP delivery remains unverified.");
                StateChanged();
                wake.Set();
              }

              Log.Warning("Native rotator position reply timeout: transport={Transport}, protocol={Protocol}, misses={Misses}, feedbackAgeMs={AgeMs}, responseTimeoutMs={ResponseTimeoutMs}, motorInterlocked={SafetyLatched}, session={Attempt}; retaining connection during transient silence.",
                settings.NativeTransport,settings.NativeProtocol,misses,ageMs,
                settings.NativeReadTimeout,PositionFaulted,attempt);
              if(NativeRotatorFeedbackPolicy.ShouldReconnect(
                   misses,ageMs,hadFeedback,
                   settings.NativeFeedbackReconnectGraceMs))
                throw new TimeoutException(
                  $"Native rotator feedback unavailable for {ageMs} ms ({misses} consecutive timeouts); reconnect required",ex);
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
            if(requiresStopAfterReconnect || appliedDirection.HasValue)
            {
              safetyLatched=true;
              stopRequested=true;
            }
            requested=lastWritten=null;manualDirection=null;appliedDirection=null;lastWire=null;
          }
          motionPlanner.Reset();plannerFeedback.Reset();lastPlanTick=lastPlannerCommandTick=0;
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
    bool onUi=SynchronizationContext.Current is
      System.Windows.Forms.WindowsFormsSynchronizationContext;
    if(IsRunning && !onUi)
      stopCompleted.Wait(Math.Clamp(settings.NativeReadTimeout+100,200,1000));
    stopping=true;stop.Set();
    try{transport?.Abort();}catch{}
    wake.Set();
    // Never block the WinForms message pump on a hung vendor device or
    // worker thread. Its I/O child can be terminated independently.
    bool joined = Thread.CurrentThread != worker && !onUi && worker.Join(2500);
    if(!joined)
      Log.Warning("Native rotator worker shutdown timed out; inspect COM driver");
    // A wedged vendor serial driver may not release a blocked worker. Leave
    // its wait handles intact until process exit rather than causing an
    // ObjectDisposedException from a still-running worker.
    if(joined){wake.Dispose();stop.Dispose();stopCompleted.Dispose();}
  }
}
