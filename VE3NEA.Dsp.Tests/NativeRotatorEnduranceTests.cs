using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkyRoof;
using SkyRoof.NativeRotator;
using Xunit;
using Xunit.Abstractions;

namespace VE3NEA.Dsp.Tests;

[CollectionDefinition("RotatorEndurance", DisableParallelization = true)]
public sealed class RotatorEnduranceCollection { }

[Collection("RotatorEndurance")]
public sealed class NativeRotatorEnduranceTests
{
  private readonly ITestOutputHelper output;
  public NativeRotatorEnduranceTests(ITestOutputHelper output) => this.output = output;

  [Fact]
  public async Task IdleWorkerTwentySecondSoakHasBoundedCpuThreadsAndHandles()
  {
    using var simulator = new SimulatedGs232();
    var settings = new RotatorSettings
    {
      UseNativeDriver = true,
      Enabled = true,
      NativeTransport = RotatorTransportKind.Tcp,
      NativeProtocol = RotatorProtocolKind.Gs232,
      Host = "127.0.0.1",
      Port = (ushort)simulator.Port,
      NativePollMs = 500,
      NativeReadTimeout = 350,
      SendTimeout = 500,
      ReconnectDelay = 250
    };
    using var engine = new NativeRotatorControlEngine(settings);
    await UntilAsync(() => engine.LastReadBearing != null);

    using var process = Process.GetCurrentProcess();
    process.Refresh();
    var beforeCpu = process.TotalProcessorTime;
    int beforeThreads = process.Threads.Count;
    int beforeHandles = process.HandleCount;
    var watch = Stopwatch.StartNew();

    await Task.Delay(TimeSpan.FromSeconds(20));

    process.Refresh();
    var cpuSeconds = (process.TotalProcessorTime - beforeCpu).TotalSeconds;
    var elapsed = watch.Elapsed.TotalSeconds;
    int threadDelta = process.Threads.Count - beforeThreads;
    int handleDelta = process.HandleCount - beforeHandles;
    int polls = simulator.Commands.Count(x => x == "C2");
    int moves = simulator.Commands.Count(x => x.StartsWith("W", StringComparison.Ordinal));
    output.WriteLine($"idle duration={elapsed:F1}s cpu={cpuSeconds:F3} cpu_per_core={100*cpuSeconds/elapsed:F1}% polls={polls} motion={moves} threadDelta={threadDelta} handleDelta={handleDelta}");

    Assert.True(engine.IsRunning,"Native controller disconnected while idle");
    Assert.InRange(polls,15,80);
    Assert.Equal(0,moves);
    Assert.True(cpuSeconds < elapsed * 0.30,
      $"Unexpected >30% of one core CPU during idle: {cpuSeconds:F2}s/{elapsed:F2}s");
    Assert.InRange(threadDelta,-10,12);
    Assert.InRange(handleDelta,-30,35);
  }

  private static async Task UntilAsync(Func<bool> condition)
  {
    for (int i=0;i<60;i++)
    {
      if (condition()) return;
      await Task.Delay(50);
    }
    Assert.True(condition(),"Simulator did not provide initial feedback");
  }

  private sealed class SimulatedGs232 : IDisposable
  {
    private readonly TcpListener listener = new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource cancel = new();
    private readonly Task server;
    internal ConcurrentQueue<string> Commands { get; } = new();
    internal int Port { get; }
    internal SimulatedGs232()
    {
      listener.Start();
      Port = ((IPEndPoint)listener.LocalEndpoint).Port;
      server = Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
      try
      {
        while (!cancel.IsCancellationRequested)
        {
          using var client = await listener.AcceptTcpClientAsync(cancel.Token);
          var stream=client.GetStream();
          var bytes = new byte[128];
          var command = new StringBuilder();
          while (!cancel.IsCancellationRequested)
          {
            int size = await stream.ReadAsync(bytes,cancel.Token);
            if (size==0) break;
            for(int i=0;i<size;i++)
            {
              char c=(char)bytes[i];
              if(c is '\r' or '\n')
              {
                if(command.Length==0) continue;
                string line=command.ToString();
                command.Clear();
                Commands.Enqueue(line);
                if(line=="C2")
                  await stream.WriteAsync(Encoding.ASCII.GetBytes("AZ=90 EL=30\r"),cancel.Token);
              }
              else command.Append(c);
            }
          }
        }
      }
      catch(Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException or SocketException) { }
    }
    public void Dispose()
    {
      cancel.Cancel();
      listener.Stop();
      try { server.Wait(TimeSpan.FromSeconds(3)); }
      catch(AggregateException e) when(e.InnerExceptions.All(x => x is OperationCanceledException)) { }
      cancel.Dispose();
    }
  }
}
