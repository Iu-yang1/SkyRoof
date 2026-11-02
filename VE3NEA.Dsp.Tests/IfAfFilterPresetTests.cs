using System;
using System.ComponentModel;
using Newtonsoft.Json;
using SkyRoof.IF;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfFilterPresetTests
  {
    private const int Rate = IfAfDemodulator.Rate;
    private static float[] IfTone(double audioHz, bool lsb = false)
    {
      float[] x = new float[Rate];
      double sign = lsb ? -1 : 1;
      for (int i = 0; i < x.Length; i++)
      {
        double t = i / (double)Rate;
        x[i] = (float)(.4 * Math.Cos(2 * Math.PI * (12000 + sign * audioHz) * t));
      }
      return x;
    }
    private static double Amplitude(float[] x, double hz)
    {
      double re = 0, im = 0;
      for (int i = 12000; i < x.Length; i++)
      {
        double a = 2 * Math.PI * hz * i / Rate;
        re += x[i] * Math.Cos(a);
        im -= x[i] * Math.Sin(a);
      }
      return 2 * Math.Sqrt(re * re + im * im) / (x.Length - 12000);
    }
    private static double SidebandLevel(IfAfSettings cfg, double hz,
      IfAfMode mode = IfAfMode.Usb)
    {
      var d = new IfAfDemodulator(mode, cfg.EffectiveFmDeviationHz,
        cfg.EffectiveSsbLowHz, cfg.EffectiveSsbHighHz,
        cfg.EffectiveCwCenterHz, cfg.EffectiveCwBandwidthHz,
        cfg.EffectiveFmIfCutoffHz, cfg.EffectiveFmAfCutoffHz);
      var y = new float[Rate];
      d.Process(IfTone(hz, mode is IfAfMode.Lsb or IfAfMode.CwLsb), y);
      return Amplitude(y, hz);
    }

    [Theory]
    [InlineData(IfAfSsbBandwidth.Narrow1_2k, "1.2 kHz")]
    [InlineData(IfAfSsbBandwidth.Wide3_3k, "3.3 kHz")]
    [InlineData(IfAfCwBandwidth.UltraNarrow100, "100 Hz")]
    [InlineData(IfAfCwBandwidth.Normal500, "500 Hz")]
    [InlineData(IfAfFmBandwidth.Narrow6_25k, "6.25 kHz")]
    [InlineData(IfAfFmBandwidth.Normal12k, "12.5 kHz")]
    public void BandwidthDropdownUsesReadableUnitsAndCanRoundTrip(
      Enum preset, string label)
    {
      var converter = TypeDescriptor.GetConverter(preset.GetType());
      Assert.Equal(label, converter.ConvertToInvariantString(preset));
      Assert.Equal(preset, converter.ConvertFromInvariantString(label));
    }

    [Fact]
    public void OnlyModeBandwidthPresetSelectorsAreShownByEnums()
    {
      Assert.Equal(7, Enum.GetValues<IfAfSsbBandwidth>().Length);
      Assert.Equal(7, Enum.GetValues<IfAfCwBandwidth>().Length);
      Assert.Equal(2, Enum.GetValues<IfAfFmBandwidth>().Length);
      var defaults = new IfAfSettings();
      Assert.Equal(IfAfSsbBandwidth.Wide3k, defaults.SsbBandwidth);
      Assert.Equal(IfAfCwBandwidth.Normal500, defaults.CwBandwidth);
      Assert.Equal(IfAfFmBandwidth.Normal12k, defaults.FmBandwidth);
      Assert.Equal(3180, defaults.EffectiveSsbHighHz);
      Assert.Equal(700, defaults.EffectiveCwCenterHz);
      Assert.Equal(5500, defaults.EffectiveFmIfCutoffHz);
      Assert.Equal(3000, defaults.EffectiveFmAfCutoffHz);
    }

    [Theory]
    [InlineData(IfAfSsbBandwidth.Narrow1_2k, 1380)]
    [InlineData(IfAfSsbBandwidth.Narrow1_8k, 1980)]
    [InlineData(IfAfSsbBandwidth.Speech2_1k, 2280)]
    [InlineData(IfAfSsbBandwidth.Speech2_4k, 2580)]
    [InlineData(IfAfSsbBandwidth.Normal2_7k, 2880)]
    [InlineData(IfAfSsbBandwidth.Wide3k, 3180)]
    [InlineData(IfAfSsbBandwidth.Wide3_3k, 3480)]
    public void SsbPresetMapsToActualBandpassWidth(
      IfAfSsbBandwidth width, int highHz)
    {
      var c = new IfAfSettings { SsbBandwidth = width };
      Assert.Equal(highHz, c.EffectiveSsbHighHz);
      IfAfFilterSettings.ValidateSsb(c.EffectiveSsbLowHz,
        c.EffectiveSsbHighHz);
    }

    [Fact]
    public void NarrowSsbKeepsOneKilohertzAndRejects2400Hz()
    {
      var cfg = new IfAfSettings { SsbBandwidth = IfAfSsbBandwidth.Narrow1_2k };
      double wanted = SidebandLevel(cfg, 950);
      double outside = SidebandLevel(cfg, 2400);
      Assert.True(wanted > .25, $"SSB 950 Hz was attenuated: {wanted}");
      Assert.True(outside < wanted * .08,
        $"SSB 1.2 kHz bandpass leaked 2400 Hz: {outside}/{wanted}");
    }

    [Theory]
    [InlineData(IfAfCwBandwidth.UltraNarrow100, 100)]
    [InlineData(IfAfCwBandwidth.VeryNarrow200, 200)]
    [InlineData(IfAfCwBandwidth.Narrow300, 300)]
    [InlineData(IfAfCwBandwidth.Narrow400, 400)]
    [InlineData(IfAfCwBandwidth.Normal500, 500)]
    [InlineData(IfAfCwBandwidth.Wide800, 800)]
    [InlineData(IfAfCwBandwidth.Wide1000, 1000)]
    public void EveryCwPresetKeepsNominalPitchInRange(
      IfAfCwBandwidth preset, int width)
    {
      var cfg = new IfAfSettings { CwBandwidth = preset };
      Assert.Equal(width, cfg.EffectiveCwBandwidthHz);
      IfAfFilterSettings.ValidateCw(cfg.EffectiveCwCenterHz,
        cfg.EffectiveCwBandwidthHz);
    }

    [Theory]
    [InlineData(IfAfCwBandwidth.UltraNarrow100)]
    [InlineData(IfAfCwBandwidth.VeryNarrow200)]
    [InlineData(IfAfCwBandwidth.Narrow300)]
    [InlineData(IfAfCwBandwidth.Narrow400)]
    [InlineData(IfAfCwBandwidth.Normal500)]
    [InlineData(IfAfCwBandwidth.Wide800)]
    [InlineData(IfAfCwBandwidth.Wide1000)]
    public void CwPassbandStillPassesNominal700HzPitch(
      IfAfCwBandwidth width)
    {
      var cfg = new IfAfSettings { CwBandwidth = width };
      double gain = SidebandLevel(cfg, 700, IfAfMode.CwUsb);
      Assert.True(gain > .23,
        $"CW preset {width} suppresses nominal pitch: {gain}");
    }

    [Theory]
    [InlineData(IfAfMode.CwUsb)]
    [InlineData(IfAfMode.CwLsb)]
    public void Narrow300HzCwRejectsOffsetButPreserves700Hz(
      IfAfMode mode)
    {
      var cfg = new IfAfSettings { CwBandwidth = IfAfCwBandwidth.Narrow300 };
      double center = SidebandLevel(cfg, 700, mode);
      double adjacent = SidebandLevel(cfg, 1200, mode);
      Assert.True(center > .25, $"CW 700 Hz center lost: {center}");
      // Correcting the too-narrow legacy CW passband slightly reduces
      // rejection at 500 Hz offset; preserve at least 10.5 dB while
      // measuring the actual -3 dB width separately.
      Assert.True(adjacent < center * .30,
        $"CW narrow filter failed to reject adjacent tone: {adjacent}/{center}");
    }

    [Fact]
    public void FM6250PresetUsesNarrowDeviationAndAfFilter()
    {
      var narrow = new IfAfSettings { FmBandwidth = IfAfFmBandwidth.Narrow6_25k };
      var normal = new IfAfSettings { FmBandwidth = IfAfFmBandwidth.Normal12k };
      Assert.Equal(3000, narrow.EffectiveFmIfCutoffHz);
      Assert.Equal(2100, narrow.EffectiveFmAfCutoffHz);
      Assert.Equal(900, narrow.EffectiveFmDeviationHz);
      Assert.Equal(5500, normal.EffectiveFmIfCutoffHz);
      Assert.Equal(3000, normal.EffectiveFmAfCutoffHz);
      Assert.Equal(2500, normal.EffectiveFmDeviationHz);
      IfAfFilterSettings.ValidateFm(narrow.EffectiveFmIfCutoffHz,
        narrow.EffectiveFmAfCutoffHz);
      IfAfFilterSettings.ValidateFm(normal.EffectiveFmIfCutoffHz,
        normal.EffectiveFmAfCutoffHz);
    }

    [Theory]
    [InlineData(IfAfSsbBandwidth.Narrow1_2k, 950)]
    [InlineData(IfAfSsbBandwidth.Normal2_7k, 1400)]
    [InlineData(IfAfSsbBandwidth.Wide3k, 1650)]
    public void CalibratedSsbMinus3DbEdgesPreserveDesignWidth(
      IfAfSsbBandwidth preset, int centerTestHz)
    {
      var cfg = new IfAfSettings { SsbBandwidth = preset };
      int middle = (cfg.EffectiveSsbLowHz + cfg.EffectiveSsbHighHz) / 2;
      double center = SidebandLevel(cfg, middle);
      double edge = SidebandLevel(cfg, cfg.EffectiveSsbLowHz);
      Assert.True(center > .35, $"center gain {center}");
      Assert.InRange(20 * Math.Log10(edge / center), -4, -2);
      Assert.True(Math.Abs(centerTestHz - middle) < 250);
    }

    [Theory]
    [InlineData(IfAfCwBandwidth.UltraNarrow100)]
    [InlineData(IfAfCwBandwidth.Narrow300)]
    [InlineData(IfAfCwBandwidth.Normal500)]
    [InlineData(IfAfCwBandwidth.Wide1000)]
    public void CwThreeDbBandwidthIsApproximatelySelectedWidth(
      IfAfCwBandwidth width)
    {
      var cfg = new IfAfSettings { CwBandwidth = width };
      double center = SidebandLevel(cfg, 700, IfAfMode.CwUsb);
      int highEdge = 700 + (int)width / 2;
      double edge = SidebandLevel(cfg, highEdge, IfAfMode.CwUsb);
      Assert.InRange(20 * Math.Log10(edge / center), -5.0, -1.0);
    }

    [Fact]
    public void FilterPresetSettingsRoundTripAndLegacyJsonStillLoads()
    {
      var cfg = new IfAfSettings {
        SsbBandwidth = IfAfSsbBandwidth.Narrow1_2k,
        CwBandwidth = IfAfCwBandwidth.Narrow300,
        FmBandwidth = IfAfFmBandwidth.Narrow6_25k
      };
      string json = JsonConvert.SerializeObject(cfg);
      var clone = JsonConvert.DeserializeObject<IfAfSettings>(json)!;
      Assert.Equal(cfg.SsbBandwidth, clone.SsbBandwidth);
      Assert.Equal(cfg.CwBandwidth, clone.CwBandwidth);
      Assert.Equal(cfg.FmBandwidth, clone.FmBandwidth);
      Assert.DoesNotContain("\"SsbLowHz\"", json);
      Assert.DoesNotContain("\"FmDeviationHz\"", json);
      var old = JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"SsbLowHz\":180,\"SsbHighHz\":3300,\"FmDeviationHz\":2500}")!;
      Assert.Equal(IfAfSsbBandwidth.Wide3k, old.SsbBandwidth);
      Assert.Equal(IfAfFmBandwidth.Normal12k, old.FmBandwidth);
    }

    [Fact]
    public void InvalidCwAndFmRangesCannotReachAudioStartup()
    {
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        IfAfFilterSettings.ValidateCw(700, 3500));
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        IfAfFilterSettings.ValidateFm(1000, 3000));
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        IfAfFilterSettings.ValidateSsb(180, 1000));
    }
  }
}
