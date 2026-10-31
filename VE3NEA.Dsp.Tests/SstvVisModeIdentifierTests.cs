using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;
namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvVisModeIdentifierTests
  {
    [Theory]
    [InlineData("Robot BW 8", "VIS-8")]
    [InlineData("MP73", "VIS-16")]
    [InlineData("MP73-N", "N-VIS-24")]
    public void IdentifiesAllThreeHeaderFamiliesWithLeadingSilence(
      string modeName, string headerFormat)
    {
      var spec = Assert.Single(SstvExtraTxSpec.All, x => x.Name == modeName);
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofVisIdentify-{Guid.NewGuid():N}.wav");
      try
      {
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        using (var osc = new SstvWaveExporter.WaveOscillator(writer))
        {
          osc.Tone(0, 400);
          SstvExtraWaveExporter.EmitHeader(osc, spec);
          osc.Tone(1500, 200);
          osc.Flush();
        }
        SstvVisModeIdentity identity =
          Assert.IsType<SstvVisModeIdentity>(SstvVisModeIdentifier.DetectWave(path));
        Assert.Equal(modeName, identity.Profile.Name);
        Assert.Equal(headerFormat, identity.HeaderFormat);
        Assert.InRange(identity.LeadingAudio.TotalMilliseconds, 395, 405);
        Assert.InRange(identity.FrequencyOffsetHz, -8, 8);
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void NoiseOnlyNeverSelectsTransmitMode()
    {
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofVisNoise-{Guid.NewGuid():N}.wav");
      try
      {
        using (var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        {
          var random = new Random(5);
          byte[] pcm = new byte[48000 * 2 * 2];
          random.NextBytes(pcm);
          writer.Write(pcm, 0, pcm.Length);
        }
        Assert.Null(SstvVisModeIdentifier.DetectWave(path));
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }
  }
}
