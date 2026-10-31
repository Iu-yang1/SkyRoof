using System;
using System.IO;
using NAudio.Wave;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvFskIdProtocolTests
  {
    [Fact]
    public void FskIdSerializesSixBitLsbFirstPayloadAndXorChecksum()
    {
      const string call = "BG5JSU";
      string path = Path.Combine(Path.GetTempPath(),
        $"SkyRoofFskWire-{Guid.NewGuid():N}.wav");
      try
      {
        using (var wave = new WaveFileWriter(path, new WaveFormat(48000, 16, 1)))
        using (var outAudio = new SstvWaveExporter.WaveOscillator(wave))
        {
          SstvTxIdentifier.Append(outAudio, new SstvIdOptions(call, false, true));
          outAudio.Flush();
        }
        using var reader = new WaveFileReader(path);
        int symbols = 1 + call.Length + 2; // STX, payload, EOT, checksum
        int[] actual = new int[symbols];
        byte[] raw = new byte[(int)reader.Length];
        Assert.Equal(raw.Length, reader.Read(raw, 0, raw.Length));
        for (int item = 0; item < symbols; item++)
        {
          int data = 0;
          for (int bit = 0; bit < 6; bit++)
          {
            double centerMs = 122 + (item * 6 + bit + .5) * 22;
            int sample = (int)Math.Round(centerMs * 48);
            float[] tone = new float[240];
            for (int n = 0; n < tone.Length; n++)
            {
              int at = 2 * (sample - 120 + n);
              tone[n] = (short)(raw[at] | raw[at + 1] << 8) / 32768f;
            }
            double hz = SstvVisAfcDetector.EstimatePitch(tone);
            if (hz is > 1800 and < 2000) data |= 1 << bit;
            else Assert.InRange(hz, 2000, 2200);
          }
          actual[item] = data;
        }
        Assert.Equal(0x2A, actual[0]);
        int xor = 0;
        for (int i = 0; i < call.Length; i++)
        {
          int expected = call[i] - 0x20;
          xor ^= expected;
          Assert.Equal(expected, actual[i + 1]);
        }
        Assert.Equal(0x01, actual[symbols - 2]);
        Assert.Equal(xor, actual[symbols - 1]);
      }
      finally { try { File.Delete(path); } catch (IOException) { } }
    }
  }
}
