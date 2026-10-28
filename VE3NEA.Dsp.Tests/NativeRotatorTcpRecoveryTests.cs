using System;
using System.Collections.Concurrent;
using System.Diagnostics;
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

// Exercise the real native TCP worker against a controller simulator. Real
// motor STOP and bridge firmware bugs still require physical verification.
public sealed class NativeRotatorTcpRecoveryTests
{
  [Theory]
  [InlineData(0,500)]
  [InlineData(1,1000)]
  [InlineData(2,2000)]
  [InlineData(3,4000)]
  [InlineData(8,5000)]
  public void ReconnectPolicyNeverReturnsThirtySecondBlackout(int failures,int ms)
  {
    Assert.Equal(ms,NativeRotatorReconnectPolicy.GetDelayMilliseconds(failures,5000));
    Assert.InRange(NativeRotatorReconnectPolicy.GetDelayMilliseconds(failures,200),500,10000);
  }

  [Fact]
  public async Task ReconnectBackoffCannotBeShortenedByPeriodicUiWakeups()
  {
    // Reproduces the pre-fix 2000ms -> ~750ms attempt storm observed
    // on 2026-10-29; the policy must watch shutdown only.
    using var stopped=new ManualResetEvent(false);
    using var uiWake=new AutoResetEvent(false);
    var timer=Stopwatch.StartNew();
    Task<bool> wait=Task.Run(()=>
      NativeRotatorReconnectPolicy.WaitForRetryOrShutdown(stopped,1300));
    for(int n=0;n<5;n++)
    {
      await Task.Delay(90);
      uiWake.Set(); // MainForm's one-second Retry signal in compressed time.
      Assert.False(wait.IsCompleted);
    }
    Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(3)));
    Assert.InRange(timer.ElapsedMilliseconds,1200,3500);
  }

  [Fact]
  public async Task ShutdownInterruptsReconnectBackoffImmediately()
  {
    using var stopped=new ManualResetEvent(false);
    var timer=Stopwatch.StartNew();
    Task<bool> wait=Task.Run(()=>
      NativeRotatorReconnectPolicy.WaitForRetryOrShutdown(stopped,5000));
    await Task.Delay(80);
    stopped.Set();
    Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(1)));
    Assert.InRange(timer.ElapsedMilliseconds,0,1000);
  }

  [Fact]
  public async Task SingleDroppedPositionReplyDoesNotDestroyHealthyTcpConnection()
  {
    await using var bridge=new Bridge((session,poll) =>
      poll==2 ? null : "AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    await UntilAsync(()=>bridge.Queries>=4);
    Assert.True(driver.IsReady);
    Assert.Equal(1,bridge.Connections);
    Assert.False(driver.PositionFaulted);
  }

  [Fact]
  public async Task SeveralTransmitLikeLostRepliesDoNotReconnectOrMove()
  {
    // Suppress three position replies but keep the transport healthy.
    await using var bridge=new Bridge((session,poll) =>
      poll is >=2 and <=4 ? null : "AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    await UntilAsync(()=>bridge.Queries>=6,7500);
    await UntilAsync(()=>driver.IsReady,7500);
    Assert.Equal(1,bridge.Connections);
    Assert.False(driver.PositionFaulted);
    Assert.Equal(0,bridge.Motions);
  }

  [Fact]
  public async Task PersistentSilentTcpBridgeEventuallyReconnects()
  {
    await using var bridge=new Bridge((session,poll) =>
      session==1 && poll>=2 ? null : "AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    await UntilAsync(()=>bridge.Queries>=4,5000);
    Assert.Equal(1,bridge.Connections);
    await UntilAsync(()=>bridge.Connections>=2,16000);
    await UntilAsync(()=>driver.IsReady && driver.LastReadBearing!=null,5000);
    Assert.False(driver.PositionFaulted);
    Assert.Equal(0,bridge.Motions);
  }

  [Fact]
  public async Task TransmitOutageWhileMovingRequestsStopAndLatchesTracking()
  {
    int ptt=0;
    await using var bridge=new Bridge((session,poll)=>
      Volatile.Read(ref ptt)!=0 ? null : "AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    driver.RotateTo(new SkyRoof.Bearing(100*Math.PI/180,40*Math.PI/180));
    await UntilAsync(()=>bridge.Motions>0);
    Volatile.Write(ref ptt,1);
    await UntilAsync(()=>driver.PositionFaulted && bridge.Stops>0,8500);
    int motions=bridge.Motions;
    // Feedback recovery must NOT automatically replay a stale Track target.
    Volatile.Write(ref ptt,0);
    await UntilAsync(()=>driver.IsReady,7000);
    await Task.Delay(550);
    Assert.Equal(motions,bridge.Motions);
    Assert.True(driver.PositionFaulted);
  }

  [Fact]
  public async Task StopThenBridgeDisconnectAllowsSafeAutomaticResynchronization()
  {
    await using var bridge=new Bridge((session,poll)=>"AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    driver.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>bridge.Motions>0);
    driver.StopRotation();
    await UntilAsync(()=>bridge.Stops>0);
    bridge.DropCurrentClient();
    await UntilAsync(()=>bridge.Connections>=2,9000);
    await UntilAsync(()=>driver.IsReady,9000);
    Assert.False(driver.PositionFaulted);
    Assert.Equal(1,bridge.Motions);
  }

  [Fact]
  public async Task ConfirmedSettledTargetDoesNotLatchAfterLaterTcpDisconnect()
  {
    int reached=0;
    await using var bridge=new Bridge((session,poll)=>
      Volatile.Read(ref reached)!=0 ? "AZ=100 EL=40\r" : "AZ=90 EL=30\r",
      cmd =>
      {
        if(cmd.StartsWith("W100 040",StringComparison.Ordinal))
          Volatile.Write(ref reached,1);
      });
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    driver.RotateTo(new SkyRoof.Bearing(100*Math.PI/180,40*Math.PI/180));
    await UntilAsync(()=>bridge.Motions>0);
    // Total query count also includes pre-command polls. Wait until the
    // actual controller has processed two >=200 ms spaced readbacks of
    // THIS target, otherwise dropping TCP races the safety interlock.
    await UntilAsync(()=>driver.TargetSettledForReconnect,7500);
    Assert.True(driver.TargetSettledForReconnect,
      "Do not simulate a settled-target disconnect before the safety gate clears.");
    bridge.DropCurrentClient();
    await UntilAsync(()=>bridge.Connections>=2,9000);
    await UntilAsync(()=>driver.IsReady,9000);
    Assert.False(driver.PositionFaulted);
    Assert.Equal(1,bridge.Motions);
  }

  [Fact]
  public async Task NoFeedbackNeverAuthorizesMotionEvenWithTcpSocketConnected()
  {
    await using var bridge=new Bridge((session,poll)=>null);
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>bridge.Queries>=2);
    driver.RotateTo(new SkyRoof.Bearing(Math.PI/2,Math.PI/6));
    await UntilAsync(()=>bridge.Connections>=2,9000);
    Assert.Equal(0,bridge.Motions);
  }

  private static RotatorSettings Settings(int port)=>new()
  {
    NativeTransport=RotatorTransportKind.Tcp,
    NativeProtocol=RotatorProtocolKind.Gs232,
    DriverMode=RotatorDriverMode.Native,
    Host="127.0.0.1",
    Port=(ushort)port,
    NativePollMs=250,
    NativeReadTimeout=180,
    // Shorter grace is intentional for a deterministic CI reconnect test;
    // the production default is 15000 ms.
    NativeFeedbackReconnectGraceMs=5500,
    SendTimeout=800,
    ReconnectDelay=1000,
    PositionJumpLimitDegrees=30
  };

  private static async Task UntilAsync(Func<bool> predicate,int timeoutMs=6000)
  {
    var timer=Stopwatch.StartNew();
    while(!predicate() && timer.ElapsedMilliseconds<timeoutMs)
      await Task.Delay(25);
    Assert.True(predicate(),"Timed out waiting for simulated TCP rotator state");
  }

  private sealed class Bridge : IAsyncDisposable
  {
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource shutdown=new();
    private readonly Func<int,int,string?> responder;
    private readonly Action<string>? commandObserver;
    private readonly Task worker;
    private TcpClient? active;
    private int connections,queries,motions,stops;
    public int Port {get;}
    public int Connections=>Volatile.Read(ref connections);
    public int Queries=>Volatile.Read(ref queries);
    public int Motions=>Volatile.Read(ref motions);
    public int Stops=>Volatile.Read(ref stops);

    public Bridge(Func<int,int,string?> responder,Action<string>? commandObserver=null)
    {
      this.responder=responder;
      this.commandObserver=commandObserver;
      listener.Start();
      Port=((IPEndPoint)listener.LocalEndpoint).Port;
      worker=Task.Run(Serve);
    }

    public void DropCurrentClient()
    {
      // Force an EOF/RST on the current connection, then accept another.
      var client=Volatile.Read(ref active);
      if(client==null)return;
      try{client.Client.LingerState=new LingerOption(true,0);}catch{}
      try{client.Close();}catch{}
    }

    private async Task Serve()
    {
      try
      {
        while(!shutdown.IsCancellationRequested)
        {
          using var client=await listener.AcceptTcpClientAsync(shutdown.Token);
          Volatile.Write(ref active,client);
          int session=Interlocked.Increment(ref connections);
          var stream=client.GetStream();
          var buf=new byte[128];
          var command=new StringBuilder();
          int sessionPoll=0;
          while(!shutdown.IsCancellationRequested)
          {
            int length;
            try{length=await stream.ReadAsync(buf,shutdown.Token);}
            catch(IOException){break;}
            catch(ObjectDisposedException){break;}
            if(length==0)break;
            for(int i=0;i<length;i++)
            {
              char c=(char)buf[i];
              if(c!='\r' && c!='\n'){command.Append(c);continue;}
              if(command.Length==0)continue;
              string cmd=command.ToString();command.Clear();
              commandObserver?.Invoke(cmd);
              if(cmd=="C2")
              {
                Interlocked.Increment(ref queries);
                string? reply=responder(session,++sessionPoll);
                if(reply!=null)
                {
                  try{await stream.WriteAsync(Encoding.ASCII.GetBytes(reply),shutdown.Token);}
                  catch(IOException){break;}
                  catch(ObjectDisposedException){break;}
                }
              }
              else if(cmd=="S")Interlocked.Increment(ref stops);
              else if(cmd.StartsWith("W",StringComparison.Ordinal))
                Interlocked.Increment(ref motions);
            }
          }
          Volatile.Write(ref active,null);
        }
      }
      catch(Exception ex) when(ex is OperationCanceledException or ObjectDisposedException or SocketException){}
    }
    public async ValueTask DisposeAsync()
    {
      shutdown.Cancel();
      DropCurrentClient();
      listener.Stop();
      try{await worker.WaitAsync(TimeSpan.FromSeconds(3));}
      catch(Exception ex) when(ex is OperationCanceledException or TimeoutException){}
      shutdown.Dispose();
    }
  }
}
