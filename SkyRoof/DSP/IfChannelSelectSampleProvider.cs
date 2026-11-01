using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VE3NEA
{
  public enum AudioChannelChoice { Mix, Left, Right }

  /// <summary>IF requires a chosen physical channel: averaging out-of-phase
  /// stereo channels can destroy the signal. Mono sources remain unchanged.</summary>
  public sealed class IfChannelSelectSampleProvider : ISampleProvider
  {
    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly AudioChannelChoice selection;
    private float[] scratch = Array.Empty<float>();
    public WaveFormat WaveFormat { get; }

    public IfChannelSelectSampleProvider(ISampleProvider source, AudioChannelChoice selection)
    {
      this.source = source ?? throw new ArgumentNullException(nameof(source));
      channels = source.WaveFormat.Channels;
      if (channels < 1) throw new ArgumentException("No audio channels.", nameof(source));
      this.selection = selection;
      WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public int Read(float[] buffer, int offset, int count)
    {
      ArgumentNullException.ThrowIfNull(buffer);
      if (offset < 0 || count < 0 || offset > buffer.Length - count)
        throw new ArgumentOutOfRangeException(nameof(count));
      if (count == 0) return 0;
      int required = checked(count * channels);
      if (scratch.Length < required) scratch = new float[required];
      int read = source.Read(scratch, 0, required);
      int frames = Math.Min(count, read / channels);
      for (int n = 0; n < frames; n++)
      {
        int baseIndex = n * channels;
        buffer[offset + n] = selection switch
        {
          AudioChannelChoice.Left => scratch[baseIndex],
          AudioChannelChoice.Right => scratch[baseIndex + Math.Min(channels - 1, 1)],
          _ => MixFrame(scratch, baseIndex, channels)
        };
      }
      return frames;
    }

    public static float MixFrame(ReadOnlySpan<float> data, int offset, int channels)
    {
      if (channels < 1 || offset < 0 || offset + channels > data.Length)
        throw new ArgumentOutOfRangeException(nameof(channels));
      double value = 0;
      for (int k = 0; k < channels; k++) value += data[offset + k];
      return (float)(value / channels);
    }
  }
}
