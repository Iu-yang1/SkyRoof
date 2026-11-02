using System;
using Newtonsoft.Json;
using SkyRoof.IF;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfAudioLevelerTests
  {
    private static float[] Tone(double amplitude, double hz = 1000, int block = 480)
    {
      var audio = new float[block];
      for (int i = 0; i < audio.Length; i++)
        audio[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / 48000));
      return audio;
    }

    [Fact]
    public void LegacyMinus15DbMigratesToFmOnlyAndSsbIsIndependent()
    {
      var config = JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"GainDb\":-15}");
      Assert.NotNull(config);
      Assert.Equal(-15, config.FmGainDb);
      Assert.Equal(0, config.SsbGainDb);
      Assert.True(config.SsbCwAgcEnabled);
      string saved = JsonConvert.SerializeObject(config);
      Assert.DoesNotContain("\"GainDb\"", saved);
      Assert.Contains("\"FmGainDb\":-15", saved);
    }

    [Fact]
    public void OldHighGainIsCappedOnMigrationButNewExplicitFmGainWins()
    {
      Assert.Equal(-15, JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"GainDb\":0}")!.FmGainDb);
      Assert.Equal(-15, JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"GainDb\":-12}")!.FmGainDb);
      Assert.Equal(-20, JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"GainDb\":-20}")!.FmGainDb);
      Assert.Equal(-19, JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"FmGainDb\":-19,\"GainDb\":0}")!.FmGainDb);
      Assert.Equal(-18, JsonConvert.DeserializeObject<IfAfSettings>(
        "{\"GainDb\":0,\"FmGainDb\":-18}")!.FmGainDb);
      Assert.Equal(-15, new IfAfSettings().FmGainDb);
    }

    [Fact]
    public void SsbSlowAgcRaisesWeakVoiceWithoutFmGainChange()
    {
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      IfAfLevelResult measured = default;
      for (int b = 0; b < 400; b++)
      {
        var x = Tone(.02);
        measured = leveler.Process(x, IfAfMode.Usb);
        Assert.All(x, s => Assert.InRange(Math.Abs(s), 0f, .891f));
        Assert.Equal(0, measured.ClippedSamples);
      }
      Assert.InRange(measured.OutputRmsDbfs, -27, -20);
      Assert.InRange(measured.EffectiveGainDb, 10, 19);
    }

    [Fact]
    public void WeakRealIfToUsbDdcToAgcRecoversAudibleAfWithoutClipping()
    {
      var dsp = new IfAfDemodulator(IfAfMode.Usb);
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      IfAfLevelResult final = default;
      for (int block = 0; block < 400; block++)
      {
        var ifPcm = new float[480];
        var af = new float[480];
        for (int i = 0; i < 480; i++)
        {
          double t = (block * 480 + i) / 48000.0;
          ifPcm[i] = (float)(.024 * Math.Cos(
            2 * Math.PI * (12000 + 1200) * t));
        }
        dsp.Process(ifPcm, af);
        final = leveler.Process(af, IfAfMode.Usb);
        Assert.Equal(0, final.ClippedSamples);
        Assert.All(af, sample => Assert.InRange(Math.Abs(sample), 0f, .891f));
      }
      Assert.InRange(final.OutputRmsDbfs, -28, -19);
      Assert.InRange(final.PreLimitPeakDbfs, -27, -12);
    }

    [Fact]
    public void FmUsesFixedIndependentSafeGainNotSsbAgc()
    {
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      IfAfLevelResult fm = default;
      for (int i = 0; i < 100; i++)
        fm = leveler.Process(Tone(.6), IfAfMode.Fm);
      Assert.InRange(fm.EffectiveGainDb, -15.01, -14.99);
      Assert.InRange(fm.OutputRmsDbfs, -24, -21);
      Assert.False(fm.PeakLimited);
      Assert.Equal(0, fm.ClippedSamples);
    }

    [Fact]
    public void StrongFmDiscriminatorSpikeGetsPeakLimitedWithoutHardClipping()
    {
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      var extreme = Tone(16);
      var result = leveler.Process(extreme, IfAfMode.Fm);
      Assert.True(result.PeakLimited);
      Assert.True(result.PeakReductionDb > 5);
      Assert.True(result.PreLimitPeakDbfs > 0);
      Assert.Equal(0, result.ClippedSamples);
      Assert.All(extreme, s => Assert.InRange(Math.Abs(s), 0f, .891f));
    }

    [Fact]
    public void KeyUpSilenceDoesNotDriveAgcToMaximumGain()
    {
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      for (int i = 0; i < 300; i++)
        leveler.Process(Tone(.04), IfAfMode.CwUsb);
      var before = leveler.Process(Tone(.04), IfAfMode.CwUsb);
      for (int i = 0; i < 300; i++)
      {
        var silent = new float[480];
        var result = leveler.Process(silent, IfAfMode.CwUsb);
        Assert.Equal(0, result.ClippedSamples);
        Assert.All(silent, s => Assert.Equal(0f, s));
      }
      var after = leveler.Process(Tone(.04), IfAfMode.CwUsb);
      Assert.InRange(Math.Abs(after.EffectiveGainDb - before.EffectiveGainDb), 0, .5);
    }

    [Fact]
    public void DisablingAgcKeepsManualSsbLevelWhileFmRemainsIndependent()
    {
      var cfg = new IfAfSettings
      {
        SsbCwAgcEnabled = false,
        SsbGainDb = 6,
        FmGainDb = -15
      };
      var leveler = new IfAfAudioLeveler(cfg);
      var a = leveler.Process(Tone(.04), IfAfMode.Lsb);
      var b = leveler.Process(Tone(.04), IfAfMode.Fm);
      Assert.InRange(a.EffectiveGainDb, 5.99, 6.01);
      Assert.InRange(b.EffectiveGainDb, -15.01, -14.99);
    }

    [Fact]
    public void InvalidModeGainProfilesAreRejected()
    {
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        new IfAfAudioLeveler(new IfAfSettings { FmGainDb = 25 }));
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        new IfAfAudioLeveler(new IfAfSettings { SsbCwAgcMaxGainDb = 80 }));
    }
  }
}
