using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class NativeRotatorWorkerTests
{
  [Fact]
  public async Task IsolatedRotatorChildCrashCannotTerminateTestHost()
  {
    // CI has built SkyRoof.exe before running this functional child test.
    string? workspace=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(workspace))return;
    string exe=Path.Combine(workspace,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    Assert.True(File.Exists(exe),$"Expected isolated rotator apphost: {exe}");
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    using var transport=new IsolatedRotatorTransport(config,exe);
    await Task.Run(transport.Open);
    Assert.True(transport.Connected);
    int pid=Assert.IsType<int>(transport.WorkerPid);
    Assert.NotEqual(Environment.ProcessId,pid);
    transport.Write(Encoding.ASCII.GetBytes("C2\r"));
    byte[] buffer=new byte[256];
    int n=transport.Read(buffer);
    Assert.Contains("AZ=90",Encoding.ASCII.GetString(buffer,0,n));

    using(var process=System.Diagnostics.Process.GetProcessById(pid))
      process.Kill(entireProcessTree:true);
    for(int i=0;i<40 && transport.Connected;i++)
      await Task.Delay(50);
    Assert.False(transport.Connected);
    Assert.ThrowsAny<IOException>(()=>
      transport.Write(Encoding.ASCII.GetBytes("W100 020\r")));
  }

  [Fact]
  public void RotatorHeartbeatUsesMonotonicTimeAndFailsClosedOnStaleChild()
  {
    long frequency=System.Diagnostics.Stopwatch.Frequency;
    long now=System.Diagnostics.Stopwatch.GetTimestamp();
    Assert.False(RotatorEngineSubprocess.IsHeartbeatExpired(0,now,frequency));
    Assert.False(RotatorEngineSubprocess.IsHeartbeatExpired(
      now-4*frequency,now,frequency));
    Assert.True(RotatorEngineSubprocess.IsHeartbeatExpired(
      now-6*frequency,now,frequency));
    Assert.False(RotatorEngineSubprocess.IsHeartbeatExpired(
      now+1,now,frequency)); // impossible future timestamp: do not underflow
    Assert.False(RotatorEngineSubprocess.IsHeartbeatExpired(
      now-10*frequency,now,0));
  }

  [Fact]
  public async Task FullRotatorEngineProcessIsolatesMotionAndFailClosesAfterCrash()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string executable=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    Assert.True(File.Exists(executable));
    await using var server=new FakeGs232Bridge();
    using var proxy=new RotatorEngineSubprocess(CreateSettings(server.Port),executable);
    Assert.NotEqual(Environment.ProcessId,proxy.WorkerPid);
    for(int i=0;i<120 && !proxy.IsReady;i++)await Task.Delay(50);
    Assert.True(proxy.IsReady,"Separate motion engine must publish real telemetry");
    Assert.NotNull(proxy.LastReadBearing);
    proxy.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
    int childPid=proxy.WorkerPid;
    using(var child=System.Diagnostics.Process.GetProcessById(childPid))
      child.Kill(entireProcessTree:true);
    for(int i=0;i<120 && proxy.IsRunning;i++)await Task.Delay(50);
    Assert.False(proxy.IsRunning);
    Assert.True(proxy.PositionFaulted);
    proxy.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
    await Task.Delay(350);
    Assert.Equal(0,server.Count("W120 030"));
  }

  [Fact]
  public async Task FullEngineManagementLeasePreservesStopAndFeedbackInterlocks()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string executable=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    await using var server=new FakeGs232Bridge();
    using var proxy=new RotatorEngineSubprocess(CreateSettings(server.Port),executable);
    for(int i=0;i<120 && !proxy.IsReady;i++)await Task.Delay(50);
    Assert.True(proxy.IsReady);
    using(var lease=await proxy.AcquireManagementLeaseAsync())
    {
      Assert.NotNull(lease);
      Assert.True(await proxy.VerifyPostManagementFeedbackAsync());
      proxy.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
      await Task.Delay(300);
      Assert.Equal(0,server.Count("W120 030"));
    }
    proxy.RotateTo(new SkyRoof.Bearing(90*Math.PI/180,30*Math.PI/180));
    await UntilAsync(()=>server.Count("W090 030")>=1);
  }

  [Fact]
  public async Task FullEngineStopPreemptsQueuedManualJogsAndBoundsQueue()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string exe=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    await using var server=new FakeGs232Bridge();
    using var proxy=new RotatorEngineSubprocess(CreateSettings(server.Port),exe);
    for(int i=0;i<120 && !proxy.IsReady;i++)await Task.Delay(50);
    Assert.True(proxy.IsReady);
    // Repeated mouse events must never create an unbounded IPC queue.
    for(int i=0;i<5000;i++)
    {
      proxy.StartContinuousMove(i%2==0
        ? RotatorContinuousDirection.Right
        : RotatorContinuousDirection.Left);
      Assert.InRange(proxy.PendingControlCommands,0,64);
    }
    proxy.StopRotation();
    Assert.InRange(proxy.PendingControlCommands,0,64);
    await UntilAsync(()=>server.Count("S")>0);
    await Task.Delay(600);
    string[] commands=server.Commands.ToArray();
    int lastStop=Array.LastIndexOf(commands,"S");
    Assert.True(lastStop>=0);
    Assert.DoesNotContain(commands.Skip(lastStop+1),
      x=>x is "R" or "L" or "U" or "D");
  }

  [Fact]
  public async Task StopBypassesPendingRemoteManagementVerification()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string exe=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    int dropFeedback=0;
    await using var server=new FakeGs232Bridge(_=>
      Volatile.Read(ref dropFeedback)==0
        ? Encoding.ASCII.GetBytes("AZ=90 EL=30\r")
        : Array.Empty<byte>());
    using var proxy=new RotatorEngineSubprocess(CreateSettings(server.Port),exe);
    for(int i=0;i<120 && !proxy.IsReady;i++)await Task.Delay(50);
    Assert.True(proxy.IsReady);
    using var lease=await proxy.AcquireManagementLeaseAsync();
    Assert.NotNull(lease);
    int sentStops=server.Count("S");
    Interlocked.Exchange(ref dropFeedback,1);
    var pendingVerification=proxy.VerifyPostManagementFeedbackAsync();
    await Task.Delay(250);
    proxy.StopRotation();
    // STOP must NOT sit for four seconds behind the async C2
    // management verification on the child's IPC reader.
    await UntilAsync(()=>server.Count("S")>sentStops);
    Assert.False(await pendingVerification);
  }

  [Fact]
  public async Task SecondMotionProcessCannotOwnAnAlreadyOpenController()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string exe=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    await using var server=new FakeGs232Bridge();
    var cfg=CreateSettings(server.Port);
    using var first=new RotatorEngineSubprocess(cfg,exe);
    for(int i=0;i<120 && !first.IsReady;i++)await Task.Delay(50);
    Assert.True(first.IsReady);
    using var second=new RotatorEngineSubprocess(cfg,exe);
    for(int i=0;i<100 && !second.WorkerExited;i++)
      await Task.Delay(50);
    Assert.True(second.WorkerExited,
      "A second SkyRoof rotator process must exit without opening the existing hardware endpoint");
    Assert.False(second.IsReady);
    Assert.True(first.IsReady);
    first.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
  }

  [Fact]
  public async Task CleanWorkerShutdownReapsOldPidBeforeReplacementCanOwnDevice()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string exe=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    await using var server=new FakeGs232Bridge();
    var settings=CreateSettings(server.Port);
    using var old=new RotatorEngineSubprocess(settings,exe);
    await UntilAsync(()=>old.IsReady);
    int firstPid=old.WorkerPid;
    // This is the path used by the non-blocking settings reapply. The
    // replacement must never be created before the prior lease is reaped.
    await old.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(12));
    Assert.True(old.WorkerExited);
    // The fake endpoint accepts connections sequentially. A killed process
    // releases its OS file lock immediately, but the asynchronous TCP server
    // may still be draining its previous socket when the old PID exits.
    // Wait for that observation, not merely Process.HasExited, before trying
    // to connect a new generation to the same simulated hardware.
    var bridgeReleaseDeadline=System.Diagnostics.Stopwatch.StartNew();
    while(server.ActiveConnections!=0 &&
          bridgeReleaseDeadline.Elapsed<TimeSpan.FromSeconds(8))
      await Task.Delay(50);
    Assert.Equal(0,server.ActiveConnections);
    using var replacement=new RotatorEngineSubprocess(settings,exe);
    Assert.NotEqual(firstPid,replacement.WorkerPid);
    var acquisitionDeadline=System.Diagnostics.Stopwatch.StartNew();
    while(!replacement.IsReady &&
          !replacement.WorkerExited &&
          acquisitionDeadline.Elapsed<TimeSpan.FromSeconds(12))
      await Task.Delay(50);
    Assert.True(replacement.IsReady,
      "Replacement failed to become ready after old PID and TCP session exited. " +
      $"Old PID={firstPid}, new PID={replacement.WorkerPid}, " +
      $"new worker exited={replacement.WorkerExited}, " +
      $"status={replacement.GetStatusString()}, " +
      $"bridge accepts={server.ConnectionCount}, " +
      $"bridge active={server.ActiveConnections}.");
    replacement.RotateTo(new SkyRoof.Bearing(100*Math.PI/180,30*Math.PI/180));
    await UntilAsync(()=>server.Count("W100 030")>=1);
  }

  [Fact]
  public async Task FakeGs232BridgeSurvivesClientTcpResetAndAcceptsReplacement()
  {
    // On Windows, forcibly closing a socket can cause ReadAsync to throw
    // IOException instead of returning zero. One abandoned TCP connection
    // must never terminate the entire simulated rotator accept loop.
    await using var server=new FakeGs232Bridge();
    using(var old=new TcpClient())
    {
      old.LingerState=new LingerOption(true,0);
      await old.ConnectAsync(IPAddress.Loopback,server.Port);
      var socket=old.GetStream();
      byte[] query=Encoding.ASCII.GetBytes("C2\r");
      await socket.WriteAsync(query);
      byte[] buffer=new byte[128];
      int n=await socket.ReadAsync(buffer,0,buffer.Length)
        .WaitAsync(TimeSpan.FromSeconds(5));
      Assert.Contains("AZ=90",Encoding.ASCII.GetString(buffer,0,n));
    }
    var disconnected=System.Diagnostics.Stopwatch.StartNew();
    while(server.ActiveConnections!=0 &&
          disconnected.Elapsed<TimeSpan.FromSeconds(8))
      await Task.Delay(50);
    Assert.Equal(0,server.ActiveConnections);

    using(var next=new TcpClient())
    {
      await next.ConnectAsync(IPAddress.Loopback,server.Port)
        .WaitAsync(TimeSpan.FromSeconds(5));
      var socket=next.GetStream();
      byte[] query=Encoding.ASCII.GetBytes("C2\r");
      await socket.WriteAsync(query);
      byte[] buffer=new byte[128];
      int n=await socket.ReadAsync(buffer,0,buffer.Length)
        .WaitAsync(TimeSpan.FromSeconds(5));
      Assert.Contains("AZ=90",Encoding.ASCII.GetString(buffer,0,n));
    }
    Assert.True(server.ConnectionCount>=2,
      "The fake GS232 listener must still accept sessions after a TCP reset.");
  }

  [Fact]
  public async Task ConcurrentShutdownCallsShareOneWorkerReap()
  {
    string? root=Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
    if(string.IsNullOrWhiteSpace(root))return;
    string exe=Path.Combine(root,"SkyRoof","bin","x64","Release",
      "net10.0-windows7.0","SkyRoof.exe");
    await using var server=new FakeGs232Bridge();
    using var worker=new RotatorEngineSubprocess(CreateSettings(server.Port),exe);
    await UntilAsync(()=>worker.IsReady);
    Task first=worker.ShutdownAsync();
    Task second=worker.ShutdownAsync();
    Assert.Same(first,second);
    await Task.WhenAll(first,second).WaitAsync(TimeSpan.FromSeconds(12));
    Assert.True(worker.WorkerExited);
  }

  [Fact]
  public async Task TrackDisabledPollsAtBoundedRateWithoutMotionOrBusyLoop()
  {
    await using var server=new FakeGs232Bridge();
    var settings=CreateSettings(server.Port);
    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.IsRunning && server.Count("C2")>0);
    await Task.Delay(1300);
    int count=server.Count("C2");
    Assert.InRange(count,1,7);
    Assert.Equal(0,server.Commands.Count(x=>x.StartsWith("W",StringComparison.Ordinal)));
    Assert.NotNull(engine.LastReadBearing);
    Assert.Equal(90,engine.LastReadBearing!.AzDeg,2);
  }

  [Fact]
  public async Task StopIsQueuedOnBackgroundIoWorkerAfterMove()
  {
    await using var server=new FakeGs232Bridge();
    using var engine=new NativeRotatorControlEngine(CreateSettings(server.Port));
    await UntilAsync(()=>engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
    engine.StopRotation();
    await UntilAsync(()=>server.Count("S")>0);
    Assert.True(server.Commands.ToArray().ToList().FindIndex(x=>x=="S")>
      server.Commands.ToArray().ToList().FindIndex(x=>x=="W090 030"));
  }

  [Fact]
  public async Task Rc3040sExposesActualParkWireTimestampsOnlyAfterIo()
  {
    await using var server=new FakeGs232Bridge();
    var settings=CreateSettings(server.Port);
    settings.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    Assert.Equal(DateTime.MinValue,engine.LastParkTargetWriteUtc);
    DateTime begun=DateTime.UtcNow;
    engine.SetParkDiagnostics(true);
    engine.StopRotation();
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0 &&
      engine.LastParkTargetWriteUtc>=begun);
    int firstStops=server.Count("S");
    DateTime targetSent=engine.LastParkTargetWriteUtc;
    engine.StopRotation();
    await UntilAsync(()=>server.Count("S")>firstStops &&
      engine.LastParkStopWriteUtc>targetSent);
    Assert.True(engine.LastParkStopWriteUtc>targetSent);
    Assert.Null(engine.RequestedBearing);
    engine.SetParkDiagnostics(false);
  }

  [Fact]
  public async Task TrackOffDoesNotRepeatAcceptedTargetDuringFeedbackPolling()
  {
    await using var server=new FakeGs232Bridge();
    var settings=CreateSettings(server.Port);
    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
    await Task.Delay(1250);
    Assert.Equal(1,server.Count("W090 030"));
  }

  [Fact]
  public async Task ManualJogReleaseSendsStopAndDoesNotResume()
  {
    await using var server=new FakeGs232Bridge();
    using var engine=new NativeRotatorControlEngine(CreateSettings(server.Port));
    await UntilAsync(()=>engine.LastReadBearing!=null);
    engine.StartContinuousMove(RotatorContinuousDirection.Right);
    await UntilAsync(()=>server.Count("R")>0);
    engine.StopRotation();
    await UntilAsync(()=>server.Count("S")>0);
    int before=server.Count("R");
    await Task.Delay(750);
    Assert.Equal(before,server.Count("R"));
    Assert.Equal(1,before);
  }

  [Fact]
  public async Task StopCancelsQueuedTargetWithoutMovingAfterStop()
  {
    await using var server=new FakeGs232Bridge();
    using var engine=new NativeRotatorControlEngine(CreateSettings(server.Port));
    await UntilAsync(()=>engine.LastReadBearing!=null);
    for(int i=0;i<100;i++)
      engine.RotateTo(new SkyRoof.Bearing((90+i%10)*Math.PI/180,Math.PI/6));
    engine.StopRotation();
    await UntilAsync(()=>server.Count("S")>0);
    await Task.Delay(400);
    var history=server.Commands.ToArray();
    int lastStop=Array.FindLastIndex(history,x=>x=="S");
    Assert.True(lastStop>=0);
    Assert.DoesNotContain(history.Skip(lastStop+1),x=>x.StartsWith("W",StringComparison.Ordinal));
  }

  [Fact]
  public async Task RefusesMotionBeforeFirstValidPositionFeedback()
  {
    int enabled=0;
    await using var server=new FakeGs232Bridge(_=>
      Volatile.Read(ref enabled)==0 ? Array.Empty<byte>() :
      Encoding.ASCII.GetBytes("AZ=90 EL=30\r"));
    using var engine=new NativeRotatorControlEngine(CreateSettings(server.Port));
    await UntilAsync(()=>server.Count("C2")>=1);
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await Task.Delay(250);
    Assert.Equal(0,server.Count("W090 030"));

    Volatile.Write(ref enabled,1);
    await UntilAsync(()=>engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
  }

  [Fact]
  public async Task SuddenPositionJumpLatchesFaultAndTransmitsStop()
  {
    int reads=0;
    await using var server=new FakeGs232Bridge(_=>
      Encoding.ASCII.GetBytes(Interlocked.Increment(ref reads)==1
        ? "AZ=90 EL=30\r" : "AZ=150 EL=30\r"));
    var settings=CreateSettings(server.Port);
    settings.PositionJumpLimitDegrees=30;
    using var engine=new NativeRotatorControlEngine(settings);

    await UntilAsync(()=>engine.LastReadBearing!=null);
    await UntilAsync(()=>engine.PositionFaulted);
    await UntilAsync(()=>server.Count("S")>0);
    int before=server.Commands.Count(x=>x.StartsWith("W",StringComparison.Ordinal));
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/3,Math.PI/6));
    await Task.Delay(250);
    Assert.Equal(before,server.Commands.Count(x=>x.StartsWith("W",StringComparison.Ordinal)));
  }

  [Fact]
  public async Task DisconnectAfterMoveLatchesAndSendsStopOnReconnect()
  {
    await using var server=new FakeGs232Bridge(dropFirstMove:true);
    var settings=CreateSettings(server.Port);
    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>server.Count("W090 030")>0);
    await UntilAsync(()=>engine.PositionFaulted);
    await UntilAsync(()=>server.Count("S")>0);
    Assert.Equal(1,server.Count("W090 030"));
  }

  [Fact]
  public async Task Rc3040sManagementLeaseBlocksNewMotionWhileStopIsTransmitted()
  {
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    using var engine=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    using var lease=await engine.AcquireManagementLeaseAsync();
    Assert.NotNull(lease);
    await UntilAsync(()=>server.Count("S")>=1);
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
    engine.StartContinuousMove(RotatorContinuousDirection.Right);
    await Task.Delay(450);
    Assert.DoesNotContain(server.Commands,x=>x.StartsWith("W",StringComparison.Ordinal));
    Assert.Equal(0,server.Count("R"));
    // Even if EEPROM operation completes, motor control remains latched
    // until the operator inspects hardware and explicitly reapplies settings.
    engine.LatchAfterManagementChange();
    Assert.True(engine.PositionFaulted);
    lease!.Dispose();
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
    await Task.Delay(350);
    Assert.DoesNotContain(server.Commands,x=>x.StartsWith("W",StringComparison.Ordinal));
  }

  [Fact]
  public async Task VerifiedManagementUpdateResumesAfterFreshMotorFeedbackWithoutReconnect()
  {
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    using var engine=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    int connections=server.ConnectionCount;
    using(var lease=await engine.AcquireManagementLeaseAsync())
    {
      Assert.NotNull(lease);
      Assert.True(await engine.VerifyPostManagementFeedbackAsync());
      engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
      await Task.Delay(250);
      Assert.DoesNotContain(server.Commands,x=>x.StartsWith("W",StringComparison.Ordinal));
    }
    Assert.False(engine.PositionFaulted);
    Assert.False(engine.ManagementReviewRequired);
    Assert.True(engine.HasLiveConnection);
    Assert.Equal(connections,server.ConnectionCount);
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
    await UntilAsync(()=>server.Count("W120 030")>=1);
  }

  [Fact]
  public async Task ManagementSafetyLockDoesNotImplyTcpDisconnection()
  {
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    using var engine=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    using(var lease=await engine.AcquireManagementLeaseAsync())
    {
      Assert.NotNull(lease);
      engine.LatchAfterManagementChange();
    }
    Assert.True(engine.PositionFaulted);
    Assert.True(engine.ManagementReviewRequired);
    Assert.True(engine.HasLiveConnection);
    Assert.Contains("Management safety lock",engine.GetStatusString());
  }

  [Fact]
  public async Task Rc3040sReferencePlannerCannotRunWithoutPhysicalValidation()
  {
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    config.Rc3040sPlannerEnabled=true;
    config.Rc3040sMotionHardwareValidated=false;
    config.Rc3040sMotionMode=Rc3040sMotionProfile.SCurve;
    using var engine=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,30*Math.PI/180));
    await Task.Delay(700);
    Assert.DoesNotContain(server.Commands,
      x=>x.StartsWith("W",StringComparison.Ordinal));
    Assert.Contains("verify actual shaft",engine.GetStatusString());
  }

  [Fact]
  public async Task SavedLegacyHardwareValidationDoesNotAuthorizeNewPlanner()
  {
    await using var server=new FakeGs232Bridge();
    var settings=CreateSettings(server.Port);
    settings.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    settings.Rc3040sPlannerEnabled=true;
    settings.Rc3040sMotionHardwareValidated=true;
    settings.Rc3040sMotionMode=Rc3040sMotionProfile.Trapezoidal;
    Assert.False(settings.Rc3040sIndependentAngleVerified);

    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,65*Math.PI/180));
    await Task.Delay(850);
    Assert.DoesNotContain(server.Commands,
      x=>x.StartsWith("W",StringComparison.Ordinal));
    Assert.Contains("verify actual shaft",engine.GetStatusString());
  }

  [Theory]
  [InlineData(Rc3040sMotionProfile.PrecisionStop)]
  [InlineData(Rc3040sMotionProfile.Trapezoidal)]
  [InlineData(Rc3040sMotionProfile.SCurve)]
  public async Task PlannedRc3040sNeverChasesStalledFeedbackWithUnboundedW(
    Rc3040sMotionProfile mode)
  {
    // The bridge can acknowledge C2 polls yet never register motion.
    // This simulates a stalled motor, wrong encoder scaling, or badly
    // delayed movement rather than an idealized servo.
    await using var server=new FakeGs232Bridge();
    var config=CreateSettings(server.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    config.Rc3040sPlannerEnabled=true;
    config.Rc3040sMotionHardwareValidated=true;
    config.Rc3040sMotionMode=mode;
    config.Rc3040sPlanAzSpeed=3;
    config.Rc3040sPlanElSpeed=3;
    config.Rc3040sIndependentAngleVerified=true;
    using var engine=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    // The UI model is mutable: changing it without reapplying settings
    // must NOT silently replace an already-authorized planned route
    // with the unbounded Direct W120 endpoint.
    config.Rc3040sPlannerEnabled=false;
    config.Rc3040sMotionHardwareValidated=false;
    engine.RotateTo(new SkyRoof.Bearing(
      120*Math.PI/180,65*Math.PI/180));

    var until=System.Diagnostics.Stopwatch.StartNew();
    while(!engine.PositionFaulted && until.ElapsedMilliseconds<13500)
      await Task.Delay(50);
    Assert.True(engine.PositionFaulted,
      "Stalled C2 feedback must latch and STOP planned movement.");
    await UntilAsync(()=>server.Count("S")>0);
    var history=server.Commands.ToArray();
    var writes=history.Where(x=>x.StartsWith("W",StringComparison.Ordinal))
      .ToArray();
    Assert.NotEmpty(writes);
    foreach(var wire in writes)
    {
      Assert.True(wire.Length>=8,$"Invalid planned wire command: {wire}");
      int az=int.Parse(wire.Substring(1,3));
      int el=int.Parse(wire.Substring(5,3));
      Assert.InRange(az,88,92);
      Assert.InRange(el,28,32);
    }
    int lastStop=Array.FindLastIndex(history,x=>x=="S");
    Assert.DoesNotContain(history.Skip(lastStop+1),
      x=>x.StartsWith("W",StringComparison.Ordinal));
  }

  [Theory]
  [InlineData(Rc3040sMotionProfile.PrecisionStop)]
  [InlineData(Rc3040sMotionProfile.Trapezoidal)]
  [InlineData(Rc3040sMotionProfile.SCurve)]
  public async Task HardwareOwnedWaypointMotionNeverOverwritesInFlightTarget(
    Rc3040sMotionProfile mode)
  {
    // Simulated RC-3040S takes multiple C2 polls to complete each
    // absolute W command; a second W before arrival is a protocol risk.
    await using var server=new FakeGs232Bridge(simulateSlowFeedback:true);
    var settings=CreateSettings(server.Port);
    settings.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    settings.NativePollMs=300;
    settings.Rc3040sPlannerEnabled=true;
    settings.Rc3040sMotionHardwareValidated=true;
    settings.Rc3040sIndependentAngleVerified=true;
    settings.Rc3040sMotionMode=mode;
    settings.Rc3040sPlanAzSpeed=4;
    settings.Rc3040sPlanElSpeed=3;
    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(
      120*Math.PI/180,45*Math.PI/180));
    var deadline=System.Diagnostics.Stopwatch.StartNew();
    while(server.PlannedWrites<3 && deadline.ElapsedMilliseconds<16000)
      await Task.Delay(50);
    Assert.True(server.PlannedWrites>=3,
      $"Unexpected planner starvation in {mode}: W count={server.PlannedWrites}");
    Assert.Equal(0,server.OverlappingWaypoints);
    Assert.False(engine.PositionFaulted);
    engine.StopRotation();
    await UntilAsync(()=>server.Count("S")>=1);
  }

  [Fact]
  public async Task FragmentedC2PacketCannotPublishIncompleteElevation()
  {
    await using var bridge=new FakeGs232Bridge(
      response:_=>Encoding.ASCII.GetBytes("AZ=90 EL=60\r"),
      fragmentC2Response:true);
    var config=CreateSettings(bridge.Port);
    config.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    using var driver=new NativeRotatorControlEngine(config);
    await UntilAsync(()=>driver.IsReady && driver.LastReadBearing!=null);
    Assert.InRange(driver.LastReadBearing!.AzDeg,89.99,90.01);
    Assert.InRange(driver.LastReadBearing.ElDeg,59.99,60.01);
    Assert.False(driver.PositionFaulted);
  }

  [Fact]
  public async Task PlannedRc3040sLatchesStopOnTransmitCorrelatedFeedbackBlackout()
  {
    int txBlackout=0;
    await using var bridge=new FakeGs232Bridge(_=>
      Volatile.Read(ref txBlackout)!=0 ? Array.Empty<byte>() :
      Encoding.ASCII.GetBytes("AZ=90 EL=30\r"));
    var settings=CreateSettings(bridge.Port);
    settings.NativeProtocol=RotatorProtocolKind.Rc3040sLan;
    settings.NativePollMs=250;
    settings.NativeReadTimeout=180;
    settings.NativeFeedbackReconnectGraceMs=5500;
    settings.Rc3040sPlannerEnabled=true;
    settings.Rc3040sMotionHardwareValidated=true;
    settings.Rc3040sIndependentAngleVerified=true;
    settings.Rc3040sMotionMode=Rc3040sMotionProfile.Trapezoidal;

    using var engine=new NativeRotatorControlEngine(settings);
    await UntilAsync(()=>engine.IsReady && engine.LastReadBearing!=null);
    engine.RotateTo(new SkyRoof.Bearing(120*Math.PI/180,50*Math.PI/180));
    await UntilAsync(()=>bridge.Commands.Any(s=>s.StartsWith("W",StringComparison.Ordinal)));
    Volatile.Write(ref txBlackout,1);
    var watch=System.Diagnostics.Stopwatch.StartNew();
    while(!engine.PositionFaulted && watch.ElapsedMilliseconds<8500)
      await Task.Delay(50);
    Assert.True(engine.PositionFaulted,
      "Active planned W with lost C2 must latch STOP without waiting for long reconnect grace.");
    await UntilAsync(()=>bridge.Count("S")>0);
    int sent=bridge.Commands.Count(s=>s.StartsWith("W",StringComparison.Ordinal));
    Volatile.Write(ref txBlackout,0);
    await UntilAsync(()=>engine.IsReady);
    await Task.Delay(800);
    Assert.Equal(sent,bridge.Commands.Count(s=>s.StartsWith("W",StringComparison.Ordinal)));
    Assert.True(engine.PositionFaulted);
  }

  private static RotatorSettings CreateSettings(int port)=>new()
  {
    Enabled=true,
    UseNativeDriver=true,
    NativeProtocol=RotatorProtocolKind.Gs232,
    NativeTransport=RotatorTransportKind.Tcp,
    Host="127.0.0.1",
    Port=(ushort)port,
    NativePollMs=500,
    NativeReadTimeout=350,
    ReceiveTimeout=500,
    SendTimeout=500,
    ReconnectDelay=200
  };
  private static async Task UntilAsync(Func<bool> predicate)
  {
    for(int i=0;i<60;i++)
    {
      if(predicate())return;
      await Task.Delay(50);
    }
    Assert.True(predicate(),"Native rotator worker timed out");
  }

  private sealed class FakeGs232Bridge : IAsyncDisposable
  {
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource cancel=new();
    private readonly Task worker;
    internal ConcurrentQueue<string> Commands {get;}=new();
    private readonly Func<string,byte[]>? response;
    private readonly bool dropFirstMove;
    private readonly bool simulateSlowFeedback;
    private readonly bool fragmentC2Response;
    private int dropped,connections,activeConnections,overlappingWaypoints,plannedWrites;
    private int simulatedAz=90,simulatedEl=30,waypointAz=90,waypointEl=30,pollCounter;
    internal int OverlappingWaypoints=>Volatile.Read(ref overlappingWaypoints);
    internal int PlannedWrites=>Volatile.Read(ref plannedWrites);
    internal int ConnectionCount => Volatile.Read(ref connections);
    internal int ActiveConnections => Volatile.Read(ref activeConnections);
    internal int Port {get;}
    internal FakeGs232Bridge(Func<string,byte[]>? response=null,bool dropFirstMove=false,
      bool simulateSlowFeedback=false,bool fragmentC2Response=false)
    {
      this.response=response;
      this.dropFirstMove=dropFirstMove;
      this.simulateSlowFeedback=simulateSlowFeedback;
      this.fragmentC2Response=fragmentC2Response;
      listener.Start();
      Port=((IPEndPoint)listener.LocalEndpoint).Port;
      worker=Task.Run(RunAsync);
    }
    internal int Count(string text)=>Commands.Count(x=>x==text);
    private async Task RunAsync()
    {
      try
      {
        while(!cancel.IsCancellationRequested)
        {
          using var client=await listener.AcceptTcpClientAsync(cancel.Token);
          Interlocked.Increment(ref connections);
          Interlocked.Increment(ref activeConnections);
          try
          {
          var stream=client.GetStream();
          var data=new byte[256];
          var command=new StringBuilder();
          while(!cancel.IsCancellationRequested)
          {
            int n=await stream.ReadAsync(data,cancel.Token);
            bool disconnect=false;
            if(n==0)break;
            for(int i=0;i<n;i++)
            {
              char c=(char)data[i];
              if(c is '\r' or '\n')
              {
                if(command.Length==0)continue;
                string line=command.ToString();command.Clear();
                Commands.Enqueue(line);
                if(dropFirstMove && line.StartsWith("W",StringComparison.Ordinal) &&
                   Interlocked.CompareExchange(ref dropped,1,0)==0)
                  disconnect=true;
                if(simulateSlowFeedback &&
                   line.StartsWith("W",StringComparison.Ordinal) &&
                   line.Length>=8 &&
                   int.TryParse(line.Substring(1,3),out int goalAz) &&
                   int.TryParse(line.Substring(5,3),out int goalEl))
                {
                  if(simulatedAz!=waypointAz || simulatedEl!=waypointEl)
                    Interlocked.Increment(ref overlappingWaypoints);
                  waypointAz=goalAz;
                  waypointEl=goalEl;
                  Interlocked.Increment(ref plannedWrites);
                }
                if(simulateSlowFeedback && line=="S")
                {
                  waypointAz=simulatedAz;
                  waypointEl=simulatedEl;
                }
                if(line=="C2")
                {
                  byte[] reply;
                  if(simulateSlowFeedback)
                  {
                    if(++pollCounter%2==0)
                    {
                      simulatedAz+=Math.Sign(waypointAz-simulatedAz);
                      simulatedEl+=Math.Sign(waypointEl-simulatedEl);
                    }
                    reply=Encoding.ASCII.GetBytes(
                      $"AZ={simulatedAz} EL={simulatedEl}\r");
                  }
                  else reply=response?.Invoke(line) ??
                    Encoding.ASCII.GetBytes("AZ=90 EL=30\r");
                  if(reply.Length>2 && fragmentC2Response)
                  {
                    // First TCP segment ends in the first EL digit;
                    // C2 must not complete before the numeric field
                    // and the CR frame terminator actually arrive.
                    await stream.WriteAsync(
                      reply.AsMemory(0,reply.Length-2),cancel.Token);
                    await Task.Delay(60,cancel.Token);
                    await stream.WriteAsync(
                      reply.AsMemory(reply.Length-2),cancel.Token);
                  }
                  else if(reply.Length>0)
                    await stream.WriteAsync(reply,cancel.Token);
                }
              }
              else command.Append(c);
            }
            if(disconnect)break;
          }
          }
          catch(Exception e) when(e is IOException or SocketException or ObjectDisposedException)
          {
            // A worker process can close its socket via TCP RST rather than
            // a graceful EOF on Windows. The bridge must keep accepting
            // replacement workers; a per-session socket fault must NEVER
            // tear down the simulated hardware listener.
          }
          finally
          {
            // The previous socket is no longer serviced by the bridge.
            // The replacement-ownership test observes this transition
            // instead of racing the fake controller's accept loop.
            Interlocked.Decrement(ref activeConnections);
          }
        }
      }
      catch(Exception e) when (e is OperationCanceledException or ObjectDisposedException or IOException or SocketException){}
    }
    public async ValueTask DisposeAsync()
    {
      cancel.Cancel();
      listener.Stop();
      try{await worker.WaitAsync(TimeSpan.FromSeconds(3));}catch(OperationCanceledException){}
      cancel.Dispose();
    }
  }
}
