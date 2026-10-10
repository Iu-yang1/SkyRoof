using System.Threading;
using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwDisplayFrameProcessorTests
  {
    private static readonly DateTime Now =
      new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

    private static CwAudioSnapshot Snapshot(long end)
    {
      float[] pcm = new float[9600];
      for (int i = 0; i < pcm.Length; i++)
        pcm[i] = (float)(0.25 *
          Math.Sin(2 * Math.PI * 850 * i / 48000.0));
      return new CwAudioSnapshot(48000, Now, end, pcm);
    }

    private static CwAudioWaterfallAnalyzer Analyzer(int n) =>
      new(sampleRate: 48000, fftSize: n,
        minFrequencyHz: 300, maxFrequencyHz: 2000,
        outputBins: 64);

    [Fact]
    public void SlowDisplayInference_DoesNotBlockOrQueueOnUiThread()
    {
      using var started = new ManualResetEventSlim();
      using var release = new ManualResetEventSlim();
      using var processor = new CwDisplayFrameProcessor(
        Analyzer(2048), Analyzer(8192),
        (pcm, _) =>
        {
          started.Set();
          release.Wait(TimeSpan.FromSeconds(5));
          return pcm;
        });

      var snapshot = Snapshot(10000);
      processor.TryQueue(snapshot, 4, CwDenoiseMode.HamNoiseV2)
        .Should().BeTrue();
      try
      {
        started.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        processor.Busy.Should().BeTrue();
        processor.RecordBusyTick();
        processor.BusyTicks.Should().Be(1);
        processor.TryQueue(
          Snapshot(11000), 4, CwDenoiseMode.Bypass)
          .Should().BeFalse();
        processor.TryTake(out _).Should().BeFalse();
      }
      finally
      {
        release.Set();
      }

      CwDisplayFrameResult completed = default;
      SpinWait.SpinUntil(
        () => processor.TryTake(out completed),
        TimeSpan.FromSeconds(5)).Should().BeTrue();
      completed.TimelineGeneration.Should().Be(4);
      completed.DenoiseMode.Should().Be(CwDenoiseMode.HamNoiseV2);
      completed.Waterfall.EndSampleIndex.Should().Be(10000);
      completed.Spectrum.EndSampleIndex.Should().Be(10000);
      completed.ProcessingMilliseconds.Should().BeGreaterOrEqualTo(0);
      processor.MeanProcessingMilliseconds.Should().BeGreaterOrEqualTo(0);
      processor.FinishedFrames.Should().Be(1);
      processor.Busy.Should().BeFalse();
    }

    [Fact]
    public void EveryFinishedFrame_IncludesHighResolutionSpectrum()
    {
      using var processor = new CwDisplayFrameProcessor(
        Analyzer(2048), Analyzer(8192));
      var snapshot = Snapshot(9600);
      processor.TryQueue(snapshot, 8, CwDenoiseMode.Bypass)
        .Should().BeTrue();
      CwDisplayFrameResult result = default;
      SpinWait.SpinUntil(
        () => processor.TryTake(out result),
        TimeSpan.FromSeconds(5)).Should().BeTrue();
      result.Waterfall.PowerDb.Should().HaveCount(64);
      result.Spectrum.PowerDb.Should().HaveCount(64);
      result.Waterfall.EndSampleIndex.Should().Be(
        result.Spectrum.EndSampleIndex);
      result.Waterfall.PowerDb.Should().Contain(
        x => float.IsFinite(x));
      result.Spectrum.PowerDb.Should().Contain(
        x => float.IsFinite(x));
    }

    [Fact]
    public void Dispose_WithNativeStylePendingJob_DoesNotWaitForCompletion()
    {
      using var started = new ManualResetEventSlim();
      using var release = new ManualResetEventSlim();
      var processor = new CwDisplayFrameProcessor(
        Analyzer(2048), Analyzer(8192),
        (pcm, _) =>
        {
          started.Set();
          release.Wait(TimeSpan.FromSeconds(5));
          return pcm;
        });
      try
      {
        processor.TryQueue(
          Snapshot(10000), 1, CwDenoiseMode.Bypass)
          .Should().BeTrue();
        started.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        processor.Dispose();
        processor.TryQueue(
          Snapshot(12000), 1, CwDenoiseMode.Bypass)
          .Should().BeFalse();
      }
      finally
      {
        release.Set();
        processor.Dispose();
      }
    }
  }
}
