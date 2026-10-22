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
  private readonly Thread worker;
  private IRotatorTransport? transport;
  private Bearing? requested,lastRead,lastWritten;
  private DateTime lastReadUtc = DateTime.MinValue;
  private bool stopping,connected,stopRequested,safetyLatched,requiresStopAfterReconnect;
  private int? manualDirection,appliedDirection;
  private byte[]? lastWire;
  private string? error;
  public event EventHandler? StatusChanged;
  public event EventHandler? BearingChanged;
  public event EventHandler<RotatorPositionFaultEventArgs>? PositionFaultDetected;

  public Bearing? RequestedBearing {get {lock(sync)return requested;}}
  public Bearing? LastReadBearing {get {lock(sync)return lastRead;}}
  public Bearing? LastWrittenBearing {get {lock(sync)return lastWritten;}}
  public DateTime LastSuccessfulBearingReadUtc {get {lock(sync)return lastReadUtc;}}
  public bool PositionFaulted {get {lock(sync)return safetyLatched;}}
  public bool IsRunning {get {lock(sync)return connected;}}
  public string GetStatusString()=>IsRunning?"Connected":"Not connected";
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
  private void StateChanged()=>Post(()=>StatusChanged?.Invoke(this,EventArgs.Empty));
  public void Retry()=>wake.Set();
  public void RotateTo(Bearing bearing)
  {
    if(!double.IsFinite(bearing.AzDeg)||!double.IsFinite(bearing.ElDeg))return;
    if(bearing.AzDeg<settings.MinAzimuth||bearing.AzDeg>settings.MaxAzimuth ||
       bearing.ElDeg<settings.MinElevation||bearing.ElDeg>settings.MaxElevation)return;
    lock(sync)
    {
      if(stopping || safetyLatched)return;
      if(!NativeRotatorCodec.CanQuery(settings.NativeProtocol) && !settings.NativeAllowOpenLoopMotion)return;
      if(!NativeRotatorCodec.CanStop(settings.NativeProtocol,settings.NativeStopTemplate) &&
         !settings.NativeAllowOpenLoopMotion)return;
      if(appliedDirection.HasValue||manualDirection.HasValue)stopRequested=true;
      requested=bearing;manualDirection=null;
    }
    wake.Set();
  }
  public void StartContinuousMove(RotatorContinuousDirection direction)
  {
    if(!NativeRotatorCodec.CanJog(settings.NativeProtocol))return;
    lock(sync)
    {
      if(stopping || safetyLatched)return;
      int value=(int)direction;
      if(value is not (2 or 4 or 8 or 16))return;
      if(appliedDirection.HasValue && appliedDirection!=value || lastWire!=null ||
          manualDirection.HasValue && manualDirection!=value)stopRequested=true;
      requested=lastWritten=null;manualDirection=value;lastWire=null;
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
      stopRequested=true;
    }
    wake.Set();
  }
  private void SetConnected(bool value)
  {
    bool changed;
    lock(sync){changed=connected!=value;connected=value;}
    if(changed)StateChanged();
  }
  private byte[] ReadReply(Func<byte[],bool> complete,int timeoutMs)
  {
    var pending=new List<byte>(128);
    var buffer=new byte[128];
    var deadline=Environment.TickCount64+timeoutMs;
    while(!stopping && Environment.TickCount64<deadline)
    {
      int length;
      try{length=transport!.Read(buffer);}
      catch(TimeoutException){length=0;}
      if(length>0)
      {
        pending.AddRange(buffer.AsSpan(0,length).ToArray());
        if(pending.Count>4096)throw new InvalidDataException("Rotator frame > 4096 bytes");
        if(complete(pending.ToArray()))return pending.ToArray();
      }
      else if(stopRequested)throw new OperationCanceledException("STOP pending");
      else Thread.Sleep(10);
    }
    throw new TimeoutException("Native rotator position reply timed out");
  }
  private byte[]? CommandBytes() => NativeRotatorCodec.Query(settings.NativeProtocol,settings.NativeQueryTemplate);
  private bool CanFeedback => NativeRotatorCodec.CanQuery(settings.NativeProtocol) ||
    settings.NativeProtocol==RotatorProtocolKind.CustomTemplate &&
    (!string.IsNullOrWhiteSpace(settings.NativeQueryTemplate)||settings.NativeTransport==RotatorTransportKind.Udp);
  private void SendStop()
  {
    byte[]? frame=NativeRotatorCodec.Stop(settings.NativeProtocol,settings.NativeStopTemplate);
    if(frame==null)
    {
      lock(sync){safetyLatched=true;error="STOP not supported by rotator protocol";}
      StateChanged();return;
    }
    transport!.Write(frame);
    lock(sync){appliedDirection=null;lastWire=null;lastWritten=null;requiresStopAfterReconnect=false;}
    // Serial/TCP bridge writes do NOT guarantee a physical motor STOP ACK.
  }
  private RotatorPosition Query()
  {
    var protocol=settings.NativeProtocol;
    if(protocol is RotatorProtocolKind.EasyCommII or RotatorProtocolKind.EasyCommIII)
    {
      transport!.Write(Encoding.ASCII.GetBytes("AZ\n"));
      var a=ReadReply(bytes=>NativeRotatorCodec.TryParseAxis(bytes,"AZ",out _),settings.NativeReadTimeout);
      if(!NativeRotatorCodec.TryParseAxis(a,"AZ",out double az))throw new InvalidDataException("Bad EasyComm azimuth");
      transport.Write(Encoding.ASCII.GetBytes("EL\n"));
      var e=ReadReply(bytes=>NativeRotatorCodec.TryParseAxis(bytes,"EL",out _),settings.NativeReadTimeout);
      if(!NativeRotatorCodec.TryParseAxis(e,"EL",out double el))throw new InvalidDataException("Bad EasyComm elevation");
      return new RotatorPosition(az,el);
    }
    byte[]? query=CommandBytes();
    if(query!=null)transport!.Write(query);
    var b=ReadReply(bytes=>NativeRotatorCodec.TryParse(protocol,bytes,out _,settings.NativeResponseTemplate),
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
    }
    Post(()=>BearingChanged?.Invoke(this,EventArgs.Empty));
    if(fault && previous!=null)
    {
      // Notify only after STOP has been enqueued; serial writes have no ACK.
      Post(()=>PositionFaultDetected?.Invoke(this,new RotatorPositionFaultEventArgs(
        previous,current,Math.Abs(previous.AzDeg-current.AzDeg),
        Math.Abs(previous.ElDeg-current.ElDeg),false)));
      wake.Set();
    }
  }
  private void Run()
  {
    int reconnect=Math.Clamp(settings.ReconnectDelay,200,30000);
    bool hadConnection=false;
    while(!stopping)
    {
      try
      {
        using var device=NativeTransportFactory.Create(settings);
        transport=device;
        device.Open();
        SetConnected(true);
        if(hadConnection)
        {
          lock(sync)
          {
            requested=lastWritten=null;manualDirection=null;appliedDirection=null;lastWire=null;
            safetyLatched=true;stopRequested=true;requiresStopAfterReconnect=true;
          }
        }
        hadConnection=true;reconnect=Math.Clamp(settings.ReconnectDelay,200,30000);
        long nextQuery=0;
        while(!stopping && device.Connected)
        {
          bool stopNow;int? jog;Bearing? target;
          lock(sync)
          {
            stopNow=stopRequested;stopRequested=false;
            jog=manualDirection;target=requested;
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
              // Query resolution first and use the live controller resolution.
              var reply=QueryRawSpid();
              if(!NativeRotatorCodec.TryParseSpid(reply,out _,out var azRes,out var elRes))
                throw new InvalidDataException("Missing SPID axis resolution");
              frame=NativeRotatorCodec.Point(settings.NativeProtocol,p,settings.NativePointTemplate,azRes,elRes);
            }
            else frame=NativeRotatorCodec.Point(settings.NativeProtocol,p,settings.NativePointTemplate);
            bool same;
            lock(sync){same=lastWire!=null&&lastWire.SequenceEqual(frame);}
            if(!same)
            {
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
            PublishPosition(Query());
          }
          int remaining=(int)Math.Clamp(nextQuery-Environment.TickCount64,20,1000);
          if(!CanFeedback)remaining=200;
          wake.WaitOne(remaining);
        }
      }
      catch(Exception ex)
      {
        if(!stopping)
        {
          Log.Warning(ex,"Native rotator I/O failure");
          lock(sync)
          {
            error=ex.Message;
            if(appliedDirection.HasValue||lastWire!=null)
              safetyLatched=true;
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
      if(!stopping){if(stop.WaitOne(reconnect))break;reconnect=Math.Min(30000,reconnect*2);}
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
    wake.Set();stop.WaitOne(Math.Clamp(settings.NativeReadTimeout+100,200,1000));
    stopping=true;stop.Set();
    try{transport?.Abort();}catch{}
    wake.Set();
    if(Thread.CurrentThread!=worker && !worker.Join(2500))
      Log.Warning("Native rotator worker shutdown timed out; inspect COM driver");
    wake.Dispose();stop.Dispose();
  }
}
