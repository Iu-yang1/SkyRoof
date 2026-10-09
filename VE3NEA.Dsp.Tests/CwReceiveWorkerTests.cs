using FluentAssertions;
using SkyRoof;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwReceiveWorkerTests
  {
    private const int SampleRate = 48000;
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SlowInference_DoesNotBlockTracking_AndSkipsBacklog()
    {
      var ingress = ReadyIngress();
      var session = new BlockingInferenceSession();

      using var worker = new CwReceiveWorker(
        ingress,
        TestOptions(),
        () => session,
        _ => [ConfirmedTrack()]);

      worker.Start();

      await WaitUntilAsync(
        () => session.Started);
      long cyclesAtInferenceStart =
        worker.GetStatus().TrackingCycles;

      for (int i = 1; i <= 4; i++)
      {
        AppendSilence(
          ingress,
          0.12,
          T0.AddSeconds(6 + i * 0.12));
        await Task.Delay(70);
      }

      await WaitUntilAsync(() =>
      {
        CwReceiveWorkerStatus status =
          worker.GetStatus();
        return status.SkippedInferenceWindows > 0 &&
               status.TrackingCycles >
                 cyclesAtInferenceStart;
      });

      CwReceiveWorkerStatus whileBusy =
        worker.GetStatus();
      whileBusy.InferenceBusy.Should().BeTrue();
      whileBusy.SkippedInferenceWindows
        .Should().BeGreaterThan(0);
      whileBusy.TrackingCycles
        .Should().BeGreaterThan(
          cyclesAtInferenceStart);
      session.MaxConcurrent.Should().Be(1);

      session.Release();

      await WaitUntilAsync(() =>
      {
        CwReceiveWorkerStatus status =
          worker.GetStatus();
        return !status.InferenceBusy &&
               status.CompletedInferenceWindows >= 1;
      });

      worker.GetStatus()
        .CompletedInferenceWindows
        .Should().Be(1);
      session.MaxConcurrent.Should().Be(1);
    }

    [Fact]
    public async Task TimelineChange_DiscardsInferenceStartedOnOldSource()
    {
      var ingress = ReadyIngress();
      var session = new BlockingInferenceSession();
      int published = 0;

      using var worker = new CwReceiveWorker(
        ingress,
        TestOptions(),
        () => session,
        _ => [ConfirmedTrack()]);

      worker.DecodeUpdated += (_, _) =>
        Interlocked.Increment(ref published);
      worker.Start();

      await WaitUntilAsync(
        () => session.Started);

      long generation =
        ingress.TimelineGeneration;
      ingress.Configure(
        true,
        CwReceiveAudioSource.SDR,
        "SDR-restarted");

      ingress.TimelineGeneration
        .Should().Be(generation + 1);

      session.Release();

      await WaitUntilAsync(
        () => !worker.GetStatus().InferenceBusy);

      Volatile.Read(ref published)
        .Should().Be(0);
      worker.LatestDecode.Should().BeNull();
    }

    [Fact]
    public async Task MissingModel_IsReportedWithoutStoppingTracker()
    {
      var ingress = ReadyIngress();

      using var worker = new CwReceiveWorker(
        ingress,
        TestOptions(),
        () => null,
        _ => [ConfirmedTrack()]);

      worker.Start();

      await WaitUntilAsync(() =>
        worker.GetStatus().ModelState ==
          CwReceiveModelState.NotInstalled);

      CwReceiveWorkerStatus status =
        worker.GetStatus();
      status.Running.Should().BeTrue();
      status.TrackingCycles
        .Should().BeGreaterThan(0);
      status.CompletedInferenceWindows
        .Should().Be(0);
      status.LastError.Should().BeNull();
    }

    private static CwPcmIngress ReadyIngress()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(
        true,
        CwReceiveAudioSource.SDR,
        "SDR");

      AppendSilence(
        ingress,
        6.0,
        T0.AddSeconds(6));
      return ingress;
    }

    private static void AppendSilence(
      CwPcmIngress ingress,
      double seconds,
      DateTime endUtc)
    {
      float[] samples =
        new float[
          (int)Math.Round(
            seconds * SampleRate)];

      ingress.Append(
          CwReceiveAudioSource.SDR,
          samples,
          samples.Length,
          endUtc)
        .Should().BeTrue();
    }

    private static CwSignalTrack ConfirmedTrack() =>
      new(
        Id: 1,
        FrequencyHz: 800,
        SnrDb: 12,
        DriftHzPerSecond: 0,
        FirstSeenUtc: T0,
        LastSeenUtc: T0.AddSeconds(6),
        Confirmed: true,
        Active: true,
        Ambiguous: false,
        FrequencySigmaHz: 2,
        MergeGroupId: 0,
        IdentityConfidence: 1,
        AssociationHintId: 101);

    private static CwReceiveWorkerOptions TestOptions() =>
      new()
      {
        TrackingInterval =
          TimeSpan.FromMilliseconds(40),
        DecodeWindowSeconds = 6.0,
        DecodeHopSeconds = 0.10,
        MaxDecodeLanes = 1,
        ModelRetryInterval =
          TimeSpan.FromSeconds(1)
      };

    private static async Task WaitUntilAsync(
      Func<bool> condition)
    {
      DateTime deadline =
        DateTime.UtcNow.AddSeconds(8);

      while (!condition() &&
             DateTime.UtcNow < deadline)
        await Task.Delay(20);

      condition().Should().BeTrue(
        "the receive worker condition should complete before timeout");
    }

    private sealed class BlockingInferenceSession :
      ICwInferenceSession
    {
      private readonly ManualResetEventSlim release =
        new(false);
      private int concurrent;
      private int maxConcurrent;
      private int started;
      private bool disposed;

      public bool Started =>
        Volatile.Read(ref started) != 0;

      public int MaxConcurrent =>
        Volatile.Read(ref maxConcurrent);

      public CwContinuousDecodeBatch Decode(
        CwAudioSnapshot snapshot,
        IReadOnlyList<CwSignalTrack> tracks)
      {
        Interlocked.Exchange(
          ref started,
          1);

        int now =
          Interlocked.Increment(
            ref concurrent);
        UpdateMax(now);

        try
        {
          release.Wait(
            TimeSpan.FromSeconds(8));
          return new(
            Array.Empty<DeepCwLaneResult>(),
            Array.Empty<CwTranscriptSnapshot>());
        }
        finally
        {
          Interlocked.Decrement(
            ref concurrent);
        }
      }

      public void Reset()
      {
      }

      public void Release() =>
        release.Set();

      private void UpdateMax(int value)
      {
        while (true)
        {
          int current =
            Volatile.Read(ref maxConcurrent);
          if (value <= current)
            return;

          if (Interlocked.CompareExchange(
                ref maxConcurrent,
                value,
                current) == current)
            return;
        }
      }

      public void Dispose()
      {
        if (disposed)
          return;
        disposed = true;
        release.Set();
        release.Dispose();
      }
    }
  }
}
