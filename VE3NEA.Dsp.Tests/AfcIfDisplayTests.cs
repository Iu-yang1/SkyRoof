using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FluentAssertions;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class AfcIfDisplayTests
{
  private static readonly DateTime T0 =
    new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
  private const int Fs = 48000;

  private static float[] Synthetic(params (double Hz, double Amplitude)[] tones)
  {
    var samples = new float[Fs];
    for (int i = 0; i < samples.Length; i++)
    {
      double value = 0;
      foreach (var tone in tones)
        value += tone.Amplitude * Math.Cos(2 * Math.PI * tone.Hz * i / Fs);
      samples[i] = (float)value;
    }
    return samples;
  }

  [Fact]
  public void Real12kIF_TwoAdjacentCWPeaksAreSeparatelyRetained()
  {
    using var analyzer = new AfcIfDisplayAnalyzer();
    var pcm = Synthetic((12100, .35), (12200, .12));
    var frame = analyzer.Analyze(pcm, T0, 10);
    frame.RelativeDb.Length.Should().Be(AfcIfDisplayAnalyzer.BinCount);
    frame.StepHz.Should().BeApproximately(48000.0 / 8192, 1e-9);
    int a = (int)Math.Round((100 + 6000) / frame.StepHz);
    int b = (int)Math.Round((200 + 6000) / frame.StepHz);
    frame.RelativeDb.Skip(a - 2).Take(5).Max()
      .Should().BeGreaterThan(20);
    frame.RelativeDb.Skip(b - 2).Take(5).Max()
      .Should().BeGreaterThan(10);
    frame.RelativeDb.Skip(a + 6).Take(Math.Max(1, b - a - 12)).Max()
      .Should().BeLessThan(frame.RelativeDb.Skip(b - 2).Take(5).Max());
  }

  [Fact]
  public void NoiseOnly_DoesNotCreateArtificialZeroDbFullScaleTrace()
  {
    var random = new Random(1234567);
    var pcm = new float[Fs];
    for (int i = 0; i < Fs; i++)
      pcm[i] = (float)(.025 * (random.NextDouble() * 2 - 1));
    using var analyzer = new AfcIfDisplayAnalyzer();
    var frame = analyzer.Analyze(pcm, T0, 3);
    frame.RawDb.Max().Should().BeLessThan(-20,
      "display normalization must not force each noise frame to 0 dBFS");
    frame.RelativeDb.Max().Should().BeLessThan(24,
      "noise should not be stretched into a strong carrier");
    frame.ClippingFraction.Should().Be(0);
  }

  [Fact]
  public void HaloReductionAndSmoothingNeverModifyRawPsdOrFrequencyBin()
  {
    var pcm = Synthetic((12000 + 115, .18), (12000 + 430, .03));
    using var analyzer = new AfcIfDisplayAnalyzer();
    analyzer.Configure(true, false, 0);
    var first = analyzer.Analyze(pcm, T0, 2);
    analyzer.Configure(true, true, 0);
    var second = analyzer.Analyze(pcm, T0.AddMilliseconds(100), 2);
    second.RawDb.Should().Equal(first.RawDb,
      "baseline flattening is exclusively visual and must not change FFT power");
    int center = (int)Math.Round((115 + 6000) / second.StepHz);
    second.RelativeDb.Skip(center - 2).Take(5).Max().Should().BeGreaterThan(20);
  }

  [Fact]
  public void NewAudioGenerationResetsVisualNoiseBaseline()
  {
    var low = Synthetic((12050, .002));
    var high = Synthetic((12050, .5));
    using var analyzer = new AfcIfDisplayAnalyzer();
    analyzer.Analyze(low, T0, 1);
    var updated = analyzer.Analyze(high, T0.AddSeconds(1), 2);
    using var fresh = new AfcIfDisplayAnalyzer();
    var reference = fresh.Analyze(high, T0.AddSeconds(1), 2);
    updated.NoiseFloorDb.Should().BeApproximately(reference.NoiseFloorDb, 1e-4,
      "a new IF target must not inherit previous generation display history");
  }

  [Fact]
  public void FmWideBandEnergySurvivesDisplayWithoutHalo()
  {
    var pcm = new float[Fs];
    for (int i = 0; i < pcm.Length; i++)
    {
      double t = i / (double)Fs;
      pcm[i] = (float)(.25 * Math.Cos(
        2 * Math.PI * (12000 + 200) * t +
        4 * Math.Sin(2 * Math.PI * 440 * t)));
    }
    using var analyzer = new AfcIfDisplayAnalyzer();
    analyzer.Configure(false, false, 100);
    var frame = analyzer.Analyze(pcm, T0, 1);
    frame.RelativeDb.Count(x => x > 15).Should().BeGreaterThan(6,
      "FM occupies an actual modulation band, not one artificial point");
  }

  [Fact]
  public void SpectrumView_PeakPreservingZoomAndWaterfallRenderInSta()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        using var view = new AfcSpectrumView();
        view.Size = new Size(780, 340);
        view.SetSpan(2000);
        view.SetWaterfallVisible(true);
        view.SetPeakHold(true);
        var pcm = Synthetic((12000 + 125, .3));
        using var analyzer = new AfcIfDisplayAnalyzer();
        for (int i = 0; i < 30; i++)
        {
          var frame = analyzer.Analyze(pcm, T0.AddMilliseconds(80 * i), 1);
          view.SetDisplayFrame(frame, 125, 125);
        }
        using var bitmap = new Bitmap(780, 340);
        view.DrawToBitmap(bitmap, new Rectangle(0, 0, 780, 340));
        bitmap.GetPixel(0, 0).A.Should().Be(255);
        view.SpanHz.Should().Be(2000);
        // The same control also supports spectrum-only mode and a narrow
        // minimum-height popup without replacing the actual FFT input.
        view.SetWaterfallVisible(false);
        view.Size = new Size(680, 145);
        view.DrawToBitmap(bitmap, new Rectangle(0, 0, 680, 145));
      }
      catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join(TimeSpan.FromSeconds(15)).Should().BeTrue();
    if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
  }
}
