using System;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvLongRecordingVisTests
  {
    [Theory]
    [InlineData(14.0, "Martin 2")]
    [InlineData(34.0, "Martin 2")]
    [InlineData(67.0, "MP73-N")]
    public void FindsVisAfterLongRecordingPreambleWithoutLoadingWholeWav(
      double leadingSeconds, string modeName)
    {
      var mode = Assert.Single(SstvTxModeProfile.All,
        x => x.Name == modeName);
      string file = Path.Combine(Path.GetTempPath(),
        $"SstvLeadingSpeech-{Guid.NewGuid():N}.wav");
      try
      {
        using (var wave = new WaveFileWriter(file,
          new WaveFormat(48000, 16, 1)))
        using (var osc = new SstvWaveExporter.WaveOscillator(wave))
        {
          osc.Tone(0, leadingSeconds * 1000);
          if (mode.Rgb != null)
            SstvWaveExporter.EmitVis(osc, mode.Rgb.VisCode7);
          else
            SstvExtraWaveExporter.EmitHeader(osc, mode.Extra!);
          osc.Tone(1500, 1800);
          osc.Flush();
        }
        var found = SstvVisModeIdentifier.DetectWave(file);
        Assert.True(found.HasValue);
        Assert.Equal(modeName, found.Value.Profile.Name);
        Assert.InRange(found.Value.LeadingAudio.TotalSeconds,
          leadingSeconds - .010, leadingSeconds + .010);
      }
      finally { try { File.Delete(file); } catch (IOException) { } }
    }
  }
}
