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
