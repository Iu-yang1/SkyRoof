using System;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvFskIdDecoderTests
  {
    [Theory]
    [InlineData("BG5JSU")]
    [InlineData("HB9Q")]
    [InlineData("BG5JSU/P")]
    public void RecoversExactMmsstvStationIdFromAudio(string callsign)
    {
      string file = Path.Combine(Path.GetTempPath(),
        "SkyRoofFskDecode-" + Guid.NewGuid().ToString("N") + ".wav");
      try
      {
        using (var wave = new WaveFileWriter(file,
          new WaveFormat(48000, 16, 1)))
        using (var osc = new SstvWaveExporter.WaveOscillator(wave))
        {
          osc.Tone(0, 350);
          SstvTxIdentifier.Append(osc,
            new SstvIdOptions(callsign, false, true));
          osc.Flush();
        }
        Assert.Equal(callsign, SstvFskIdDecoder.DecodeWaveSuffix(file));
      }
      finally { try { File.Delete(file); } catch (IOException) { } }
    }

    [Fact]
    public void CannotIdentifyStationFromOrdinaryVoiceOrNoise()
    {
      var noise = new float[48000];
      var random = new Random(11);
      for (int i = 0; i < noise.Length; i++)
        noise[i] = (float)(random.NextDouble() * .8 - .4);
      Assert.Null(SstvFskIdDecoder.Decode(noise));
    }
  }
}
