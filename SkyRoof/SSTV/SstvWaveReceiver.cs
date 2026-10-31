using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MathNet.Numerics;
using NAudio.Wave;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Bounded-memory 48 kHz mono SSTV recording playback into the same
  /// SkySignals streaming decoder as the live receiver. No CAT/PTT access.
  /// Useful for offline verification of real over-the-air captures and
  /// imported recordings before enabling radio transmissions.
  /// </summary>
  public static class SstvWaveReceiver
  {
    public static async Task DecodeAsync(string wavePath,
      Action<SstvImageEvent> imageReceived,
      Action<double>? progress = null,
      CancellationToken cancellationToken = default)
    {
      ArgumentNullException.ThrowIfNull(imageReceived);
      using var wave = new WaveFileReader(wavePath);
      if (wave.WaveFormat.SampleRate != 48000 ||
          wave.WaveFormat.Channels != 1 ||
          wave.WaveFormat.BitsPerSample != 16 ||
          wave.TotalTime.TotalSeconds > 420)
        throw new InvalidDataException(
          "请提供 48 kHz、16-bit、单声道且不超过七分钟的 SSTV WAV 文件。");

      await Task.Run(() =>
      {
        using var decoder = new SstvDecoder();
        decoder.ImageUpdated += imageReceived;
        decoder.ImageCompleted += imageReceived;

        // 50 ms chunks: no giant IQ array for a 5-minute picture.
        byte[] buffer = new byte[4800];
        double phase = 0;
        const double fmStep = 2 * Math.PI * 3300 / 48000;
        int count;
        while ((count = wave.Read(buffer, 0, buffer.Length)) > 0)
        {
          cancellationToken.ThrowIfCancellationRequested();
          int frames = count / 2;
          var iq = new Complex32[frames];
          for (int i = 0; i < frames; i++)
          {
            short sample = (short)(buffer[2 * i] | buffer[2 * i + 1] << 8);
            phase += fmStep * (sample / 32768.0);
            if (phase > Math.PI) phase -= 2 * Math.PI;
            if (phase < -Math.PI) phase += 2 * Math.PI;
            iq[i] = new Complex32((float)Math.Cos(phase), (float)Math.Sin(phase));
          }
          decoder.Process(iq);
          progress?.Invoke((double)wave.Position / wave.Length);
        }
        decoder.Flush();
      }, cancellationToken);
    }
  }
}
