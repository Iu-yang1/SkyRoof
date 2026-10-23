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
  public async Task ThreeConsecutiveMissingRepliesReconnectAutomatically()
  {
    await using var bridge=new Bridge((session,poll) =>
      session==1 && poll>=2 ? null : "AZ=90 EL=30\r");
    using var driver=new NativeRotatorControlEngine(Settings(bridge.Port));
    await UntilAsync(()=>driver.IsReady);
    var watch=Stopwatch.StartNew();
    await UntilAsync(()=>bridge.Connections>=2,9000);
    await UntilAsync(()=>driver.IsReady && driver.LastReadBearing!=null,9000);
    Assert.True(watch.ElapsedMilliseconds<9000,
      $"Recovery exceeded budget: {watch.ElapsedMilliseconds}ms");
    Assert.False(driver.PositionFaulted);
    Assert.Equal(0,bridge.Motions);
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
    await UntilAsync(()=>bridge.Queries>=5,7500);
    // Two separate readings at the commanded position clear the
    // may-still-be-moving interlock; no continuous jog was requested.
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
