using System;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvLiveFskIdDetectorTests
  {
    [Theory]
    [InlineData("BG5JSU")]
    [InlineData("HB9Q")]
    public void LiveAudioDetectsOneCheckedFskIdWithArbitraryChunks(string call)
    {
      string path = Path.Combine(Path.GetTempPath(),
        $"SstvLiveFsk-{Guid.NewGuid():N}.wav");
      try
      {
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        using (var wave = new SstvWaveExporter.WaveOscillator(writer))
        {
          wave.Tone(0, 360);
          SstvTxIdentifier.Append(wave, new SstvIdOptions(call, false, true));
          wave.Tone(0, 600);
          wave.Flush();
        }
        using var stream = new WaveFileReader(path);
        var rx = new SstvLiveFskIdDetector();
        int events = 0;
        string last = "";
        rx.StationIdentified += value => { events++; last = value; };
        var bytes = new byte[3352];
        int length;
        while ((length = stream.Read(bytes, 0, bytes.Length)) > 0)
        {
          int samples = length / 2;
          var af = new float[samples];
          for (int n = 0; n < samples; n++)
            af[n] = (short)(bytes[n * 2] | bytes[n * 2 + 1] << 8) / 32768f;
          rx.Process(af);
        }
        Assert.Equal(1, events);
        Assert.Equal(call, last);
        rx.Reset();
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void SilenceIsNotStationId()
    {
      var rx = new SstvLiveFskIdDetector();
      int events = 0;
      rx.StationIdentified += _ => events++;
      float[] silence = new float[48000];
      for (int i = 0; i < 8; i++) rx.Process(silence);
      Assert.Equal(0, events);
    }
  }
}
