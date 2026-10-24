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
    await UntilAsync(()=>server.Count("W120 030")>0);
    Assert.Equal(1,server.Count("W120 030")); // baseline direct path
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
    private int dropped,connections;
    internal int ConnectionCount => Volatile.Read(ref connections);
    internal int Port {get;}
    internal FakeGs232Bridge(Func<string,byte[]>? response=null,bool dropFirstMove=false)
    {
      this.response=response;
      this.dropFirstMove=dropFirstMove;
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
                if(line=="C2")
                {
                  var reply=response?.Invoke(line) ??
                    Encoding.ASCII.GetBytes("AZ=90 EL=30\r");
                  if(reply.Length>0)
                    await stream.WriteAsync(reply,cancel.Token);
                }
              }
              else command.Append(c);
            }
            if(disconnect)break;
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
