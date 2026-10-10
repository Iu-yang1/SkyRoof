using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwIncrementalStftCacheTests
  {
    private const int Rate = 48000;
    private static float[] Tone(int samples)
    {
      var data = new float[samples];
      for (int i = 0; i < samples; i++)
        data[i] = (float)(0.25 * Math.Sin(2 * Math.PI * 735 * i / Rate));
      return data;
    }

    [Fact]
    public void RidgeCache_ReusesExactAbsoluteFramesWithoutChangingObservations()
    {
      var opts = new CwFrameRidgeScannerOptions
      {
        SampleRate = Rate, MinFrequencyHz = 400, MaxFrequencyHz = 1500
      };
      var cached = new CwFrameRidgeScanner(opts)
      {
        EnableIncrementalCache = true
      };
      var reference = new CwFrameRidgeScanner(opts);
      float[] all = Tone(115200 + 5760);
      var first = new CwAudioSnapshot(
        Rate, DateTime.UtcNow, 115200, all[..115200]);
      var second = new CwAudioSnapshot(
        Rate, DateTime.UtcNow, 120960, all[5760..]);
      cached.Scan(first);
      var actual = cached.Scan(second);
      var expected = reference.Scan(second);
      cached.StftCacheHits.Should().BeGreaterThan(100);
      actual.FastObservations.Should().Equal(expected.FastObservations);
      actual.PrecisionBatches.SelectMany(x => x.Observations)
        .Should().Equal(
          expected.PrecisionBatches.SelectMany(x => x.Observations));

      cached.ResetCache();
      cached.StftCacheEntries.Should().Be(0);
      cached.StftCacheHits.Should().Be(0);
    }

    [Fact]
    public void WidebandCache_ReusesOnlyMatchingInteriorSamples()
    {
      var metadata = new DeepCwModelMetadata
      {
        Chars = ["A"], BlankIndex = 1, NumClasses = 2,
        SampleRate = 3200, FftLength = 256, HopLength = 48,
        MinFrequencyHz = 400, MaxFrequencyHz = 1200,
        FrequencyBins = 65, Normalization = "log1p",
        InputName = "spectrogram", OutputName = "log_probs",
        InputLayout = ["batch", "channel", "time", "frequency"],
        OutputLayout = ["batch", "time", "class"],
        ChannelCount = 1, InputDtype = "float32", OutputDtype = "float32"
      };
      float[] all = Tone(9 * Rate);
      var cache = new CwStftMagnitudeCache(1600);
      for (int step = 0; step < 4; step++)
      {
        float[] window = all.AsSpan(step * Rate, 6 * Rate).ToArray();
        long absoluteEnd = (6L + step) * Rate;
        var uncached = DeepCwWidebandFeatureWindow.Create(
          window, Rate, metadata);
        var cached = DeepCwWidebandFeatureWindow.Create(
          window, Rate, metadata, absoluteEnd, cache);
        for (int frame = 0; frame < cached.FrameCount; frame++)
          foreach (double hz in new[] { 400.0, 735.0, 1000.0 })
            cached.SampleCalibratedMagnitude(frame, hz)
              .Should().Be(uncached.SampleCalibratedMagnitude(frame, hz));
      }
      // +3s revisits the same 15-ms lattice, but only ~3 seconds
      // overlap and window boundaries remain deliberately uncached.
      cache.Hits.Should().BeGreaterThan(150);
      cache.Count.Should().BeLessOrEqualTo(1600);
    }

    [Fact]
    public void StftCache_IsBoundedAndResettable()
    {
      var cache = new CwStftMagnitudeCache(3);
      for (int i = 0; i < 5; i++)
        cache.Save(i * 720, Rate, 3840, new float[] { i, 1, 2 });
      cache.Count.Should().Be(3);
      var row = new float[3];
      cache.TryCopy(0, Rate, 3840, row).Should().BeFalse();
      cache.TryCopy(4 * 720, Rate, 3840, row).Should().BeTrue();
      row[0].Should().Be(4);
      cache.Clear();
      cache.Count.Should().Be(0);
    }
  }
}
