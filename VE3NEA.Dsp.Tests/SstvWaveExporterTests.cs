using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using VE3NEA.SkySSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvWaveExporterTests
  {
    [Theory]
    [InlineData(SstvMode.Robot36)]
    [InlineData(SstvMode.Pd50)]
    public void OfflineWavHasVisAndExpectedRasterDuration(SstvMode mode)
    {
      var spec = SstvModes.Get(mode);
      using var input = new Bitmap(64, 64);
      using (Graphics g = Graphics.FromImage(input))
      {
        g.Clear(Color.CornflowerBlue);
        g.FillRectangle(Brushes.White, 0, 0, 32, 64);
      }
      string file = Path.Combine(Path.GetTempPath(), $"skyroof-sstv-{Guid.NewGuid():N}.wav");
      try
      {
        SstvWaveExporter.Export(input, mode, file);
        using var wave = new WaveFileReader(file);
        Assert.Equal(48000, wave.WaveFormat.SampleRate);
        Assert.Equal(1, wave.WaveFormat.Channels);
        Assert.Equal(16, wave.WaveFormat.BitsPerSample);
        double expectedSeconds = (SstvTones.VisHeaderMs +
          spec.LineCount * spec.LinePeriodMs) / 1000.0;
        Assert.InRange(wave.TotalTime.TotalSeconds,
          expectedSeconds - 0.01, expectedSeconds + 0.01);

        // A leader @1900 Hz must appear before the raster and without clipping.
        byte[] pcm = new byte[48000 / 10 * 2];
        int n = wave.Read(pcm, 0, pcm.Length);
        Assert.Equal(pcm.Length, n);
        double inPhase = 0, quadrature = 0;
        const int frequency = 1900;
        for (int i = 0; i < n / 2; i++)
        {
          short sample = BitConverter.ToInt16(pcm, 2 * i);
          Assert.InRange(sample, (short)-26000, (short)26000);
          double phase = 2 * Math.PI * frequency * i / 48000.0;
          inPhase += sample * Math.Cos(phase);
          quadrature += sample * Math.Sin(phase);
        }
        Assert.True(Math.Sqrt(inPhase * inPhase + quadrature * quadrature) >
          2000.0 * (n / 2), "VIS leader must contain the expected 1900 Hz tone");
      }
      finally
      {
        try { File.Delete(file); } catch (IOException) { }
      }
    }
  }
}
