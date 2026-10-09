using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class CwAudioAndDetectorTests
  {
    private const int SampleRate = 48000;
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AudioHub_KeepsOnlyNewestBoundedSamples()
    {
      var hub = new CwAudioHub(1000, 1.0);
      float[] first = Enumerable.Range(0, 700).Select(i => (float)i).ToArray();
      float[] second = Enumerable.Range(700, 700).Select(i => (float)i).ToArray();

      hub.Append(first, first.Length, T0);
      hub.Append(second, second.Length, T0.AddMilliseconds(700));

      hub.BufferedSamples.Should().Be(1000);
      hub.TotalSamplesWritten.Should().Be(1400);
      hub.TrySnapshot(1.0, out CwAudioSnapshot snapshot).Should().BeTrue();
      snapshot.Samples.First().Should().Be(400);
      snapshot.Samples.Last().Should().Be(1399);
      snapshot.EndUtc.Should().Be(T0.AddMilliseconds(700));
    }

    [Fact]
    public void Detector_FindsThreeIndependentKeyedCarriers()
    {
      float[] audio = MakeAudio(2.6,
        (620, 0.35f, 0.15, 0.55),
        (845, 0.18f, 0.20, 0.50),
        (1120, 0.28f, 0.12, 0.45));

      var detector = NewDetector();
      var found = detector.Detect(audio);

      found.Should().Contain(x => Math.Abs(x.FrequencyHz - 620) < 18);
      found.Should().Contain(x => Math.Abs(x.FrequencyHz - 845) < 18);
      found.Should().Contain(x => Math.Abs(x.FrequencyHz - 1120) < 18);
    }

    [Fact]
    public void Detector_RejectsContinuousCarrier_WhenKeyingIsRequired()
    {
      float[] audio = MakeContinuousTone(2.6, 800, 0.35f, 0.004f);
      var detector = NewDetector();

      detector.Detect(audio)
        .Should().NotContain(x => Math.Abs(x.FrequencyHz - 800) < 25);
    }

    [Fact]
    public void Detector_NoiseOnly_DoesNotCreatePileup()
    {
      float[] audio = MakeNoise(2.6, 0.008f);
      var detector = NewDetector();

      detector.Detect(audio).Should().BeEmpty();
    }

    [Fact]
    public void PileupFrontEnd_ProducesStableConfirmedLanesAcrossWindows()
    {
      var frontEnd = new CwPileupFrontEnd(
        SampleRate, 2.4, DetectorOptions(),
        new CwPileupTrackManager(matchToleranceHz: 45));

      float[] first = MakeAudio(2.4,
        (700, 0.30f, 0.14, 0.50),
        (980, 0.22f, 0.17, 0.48));
      frontEnd.AddSamples(first, first.Length, T0);
      var initial = frontEnd.Analyze();
      initial.Should().HaveCount(
        2,
        "initial tracks were: {0}",
        string.Join(
          ", ",
          initial.Select(x =>
            $"#{x.Id} {x.FrequencyHz:F1}Hz {x.SnrDb:F1}dB")));

      float[] second = MakeAudio(0.8,
        (708, 0.16f, 0.14, 0.50),
        (972, 0.34f, 0.17, 0.48));
      frontEnd.AddSamples(second, second.Length, T0.AddMilliseconds(800));
      var updated = frontEnd.Analyze();

      updated.Should().HaveCount(2);
      updated.Select(x => x.Id).Should()
        .BeEquivalentTo(initial.Select(x => x.Id));
      updated.Should().OnlyContain(x => x.Confirmed);
    }

    private static CwCandidateDetector NewDetector() =>
      new(DetectorOptions());

    private static CwDetectorOptions DetectorOptions() => new()
    {
      SampleRate = SampleRate,
      FftSize = 4096,
      HopSize = 1024,
      MinFrequencyHz = 300,
      MaxFrequencyHz = 1500,
      MinimumSnrDb = 7,
      MinimumKeyingDepthDb = 3,
      MinActiveFraction = 0.10,
      MaxActiveFraction = 0.92,
      MinimumActivityTransitions = 2,
      PeakDeduplicationHz = 30,
      MaxCandidates = 12
    };

    private static float[] MakeAudio(
      double seconds,
      params (double Hz, float Amplitude, double KeyPeriod, double Duty)[] tones)
    {
      int count = (int)Math.Round(seconds * SampleRate);
      float[] data = MakeNoise(seconds, 0.004f);
      for (int n = 0; n < count; n++)
      {
        double t = n / (double)SampleRate;
        foreach (var tone in tones)
        {
          bool keyDown = (t % tone.KeyPeriod) < tone.KeyPeriod * tone.Duty;
          if (keyDown)
            data[n] += tone.Amplitude *
              (float)Math.Sin(2 * Math.PI * tone.Hz * t);
        }
      }
      return data;
    }

    private static float[] MakeContinuousTone(
      double seconds, double hz, float amplitude, float noiseAmplitude)
    {
      float[] data = MakeNoise(seconds, noiseAmplitude);
      for (int n = 0; n < data.Length; n++)
      {
        double t = n / (double)SampleRate;
        data[n] += amplitude * (float)Math.Sin(2 * Math.PI * hz * t);
      }
      return data;
    }

    private static float[] MakeNoise(double seconds, float amplitude)
    {
      int count = (int)Math.Round(seconds * SampleRate);
      var random = new Random(1234567);
      var data = new float[count];
      for (int i = 0; i < count; i++)
        data[i] = amplitude * (float)(2 * random.NextDouble() - 1);
      return data;
    }
  }
}
