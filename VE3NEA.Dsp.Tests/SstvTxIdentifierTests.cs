using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvTxIdentifierTests
  {
    [Theory]
    [InlineData("Robot 36")]
    [InlineData("Scottie 1")]
    [InlineData("MP73")]
    [InlineData("MC110-N")]
    public void AudioCwAndFskIdAppendToEveryEncoderFamily(string modeName)
    {
      var mode = Assert.Single(SstvTxModeProfile.All, m => m.Name == modeName);
      using var source = new Bitmap(12, 12);
      string plain = Path.Combine(Path.GetTempPath(),
        $"SkyRoofSstvID-plain-{Guid.NewGuid():N}.wav");
      string id = Path.Combine(Path.GetTempPath(),
        $"SkyRoofSstvID-appended-{Guid.NewGuid():N}.wav");
      try
      {
        SstvRgbWaveExporter.Export(source, mode, plain);
        SstvRgbWaveExporter.Export(source, mode, id,
          stationId: new SstvIdOptions("BG5JSU", true, true));
        using var a = new WaveFileReader(plain);
        using var b = new WaveFileReader(id);
        Assert.True(b.TotalTime.TotalSeconds > a.TotalTime.TotalSeconds + 2);
        Assert.Equal(a.WaveFormat.SampleRate, b.WaveFormat.SampleRate);
        Assert.Equal(a.WaveFormat.Channels, b.WaveFormat.Channels);
        Assert.Equal(16, b.WaveFormat.BitsPerSample);
      }
      finally
      {
        try { File.Delete(plain); } catch (IOException) { }
        try { File.Delete(id); } catch (IOException) { }
      }
    }

    [Fact]
    public void QuietCwGapsContainExactDigitalSilence()
    {
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofSstvID-CW-{Guid.NewGuid():N}.wav");
      try
      {
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        using (var osc = new SstvWaveExporter.WaveOscillator(wave))
        {
          SstvTxIdentifier.Append(osc,
            new SstvIdOptions("BG5JSU", true, false));
          osc.Flush();
        }
        using var reader = new WaveFileReader(path);
        var silence = new byte[4800];
        Assert.Equal(silence.Length, reader.Read(silence, 0, silence.Length));
        Assert.All(silence, x => Assert.Equal((byte)0, x));
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void InvalidStationIdCannotGenerateUnauthenticatedFsk()
    {
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofSstvID-Invalid-{Guid.NewGuid():N}.wav");
      try
      {
        using var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 1));
        using var osc = new SstvWaveExporter.WaveOscillator(wave);
        Assert.Throws<ArgumentException>(() =>
          SstvTxIdentifier.Append(osc,
            new SstvIdOptions("BAD CALL", false, true)));
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }
  }
}
