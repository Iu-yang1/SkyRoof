using FluentAssertions;
using SkyRoof.CW;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwAudioWaterfallAnalyzerTests
  {
    private const int SampleRate = 48000;
    private static readonly DateTime T0 =
      new(
        2026, 10, 9,
        0, 0, 0,
        DateTimeKind.Utc);

    [Fact]
    public void Analyzer_PlacesSingleToneAtCorrectAfFrequency()
    {
      const double toneHz = 843;
      float[] samples =
        new float[5760];

      for (int i = 0;
           i < samples.Length;
           i++)
      {
        samples[i] =
          0.3f *
          (float)Math.Sin(
            2 * Math.PI *
            toneHz * i /
            SampleRate);
      }

      var analyzer =
        new CwAudioWaterfallAnalyzer(
          SampleRate,
          fftSize: 4096,
          minFrequencyHz: 100,
          maxFrequencyHz: 2000,
          outputBins: 384);

      CwAudioSpectrumFrame frame =
        analyzer.Analyze(
          new CwAudioSnapshot(
            SampleRate,
            T0,
            samples.Length,
            samples));

      int peakIndex =
        frame.PowerDb
          .Select((value, index) =>
            (value, index))
          .OrderByDescending(x => x.value)
          .First()
          .index;

      double peakHz =
        frame.MinFrequencyHz +
        peakIndex *
        frame.BinHz;

      peakHz.Should().BeApproximately(
        toneHz,
        15);
      frame.EndSampleIndex
        .Should().Be(samples.Length);
      frame.PowerDb
        .Should().OnlyContain(
          value => float.IsFinite(value));
    }

    [Fact]
    public void Analyzer_RejectsSnapshotShorterThanOneFft()
    {
      var analyzer =
        new CwAudioWaterfallAnalyzer(
          SampleRate,
          fftSize: 4096);

      Action act =
        () => analyzer.Analyze(
          new CwAudioSnapshot(
            SampleRate,
            T0,
            2000,
            new float[2000]));

      act.Should()
        .Throw<ArgumentException>();
    }

    [Fact]
    public void Analyzer_RejectsMismatchedSampleRate()
    {
      var analyzer =
        new CwAudioWaterfallAnalyzer(
          SampleRate,
          fftSize: 4096);

      Action act =
        () => analyzer.Analyze(
          new CwAudioSnapshot(
            44100,
            T0,
            5000,
            new float[5000]));

      act.Should()
        .Throw<ArgumentException>();
    }
  }
}
