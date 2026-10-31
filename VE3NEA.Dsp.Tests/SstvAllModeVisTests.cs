using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvAllModeVisTests
  {
    public static IEnumerable<object[]> AllModeNames()
    {
      foreach (SstvTxModeProfile mode in SstvTxModeProfile.All)
        yield return new object[] { mode.Name };
    }

    [Theory]
    [MemberData(nameof(AllModeNames))]
    public void EveryModeHasRecognizableVisHeader(string name)
    {
      SstvTxModeProfile profile = Assert.Single(SstvTxModeProfile.All,
        x => x.Name == name);
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofAllModeVis-{Guid.NewGuid():N}.wav");
      try
      {
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        using (var oscillator = new SstvWaveExporter.WaveOscillator(wave))
        {
          oscillator.Tone(0, 300);
          if (profile.Extra is { } extra)
            SstvExtraWaveExporter.EmitHeader(oscillator, extra);
          else if (profile.Native is { } native)
            SstvWaveExporter.EmitVis(oscillator,
              VE3NEA.SkySSTV.SstvModes.Get(native).VisCode);
          else
            SstvWaveExporter.EmitVis(oscillator, profile.Rgb!.VisCode7);
          oscillator.Tone(1500, 100);
          oscillator.Flush();
        }

        var identity = SstvVisModeIdentifier.DetectWave(path);
        Assert.True(identity.HasValue, $"Could not decode {name} VIS");
        Assert.Equal(name, identity!.Value.Profile.Name);
        Assert.InRange(identity.Value.LeadingAudio.TotalMilliseconds, 295, 305);
        Assert.InRange(identity.Value.FrequencyOffsetHz, -10, 10);
      }
      finally
      {
        try { File.Delete(path); } catch (IOException) { }
      }
    }
  }
}
