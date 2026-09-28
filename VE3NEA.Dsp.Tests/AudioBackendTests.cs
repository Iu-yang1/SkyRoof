using FluentAssertions;
using MathNet.Numerics;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class AudioBackendTests
  {
    [Fact]
    public void RingBufferWaveProvider_MonoFloat_AppliesStreamVolumeAndZeroFills()
    {
      var provider = new global::VE3NEA.RingBufferWaveProvider<float>(48_000)
      {
        Volume = 0.5f
      };

      provider.WaveFormat.SampleRate.Should().Be(48_000);
      provider.WaveFormat.Channels.Should().Be(1);
      provider.WaveFormat.BitsPerSample.Should().Be(32);

      provider.AddSamples(new[] { 1.0f, -0.5f });

      byte[] bytes = new byte[16];
      provider.Read(bytes, 0, bytes.Length).Should().Be(bytes.Length);

      BitConverter.ToSingle(bytes, 0).Should().Be(0.5f);
      BitConverter.ToSingle(bytes, 4).Should().Be(-0.25f);
      BitConverter.ToSingle(bytes, 8).Should().Be(0f);
      BitConverter.ToSingle(bytes, 12).Should().Be(0f);
    }

    [Fact]
    public void RingBufferWaveProvider_Complex32_ExposesTwoFloatChannels()
    {
      var provider = new global::VE3NEA.RingBufferWaveProvider<Complex32>(48_000)
      {
        Volume = 1f
      };

      provider.WaveFormat.Channels.Should().Be(2);
      provider.WaveFormat.BitsPerSample.Should().Be(32);

      provider.AddSamples(new[] { new Complex32(0.25f, -0.75f) });

      byte[] bytes = new byte[8];
      provider.Read(bytes, 0, bytes.Length).Should().Be(bytes.Length);

      BitConverter.ToSingle(bytes, 0).Should().Be(0.25f);
      BitConverter.ToSingle(bytes, 4).Should().Be(-0.75f);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    public void RingBufferWaveProvider_RejectsOutOfRangeVolume(float volume)
    {
      var provider = new global::VE3NEA.RingBufferWaveProvider<float>(48_000);

      Action setVolume = () => provider.Volume = volume;

      setVolume.Should().Throw<ArgumentOutOfRangeException>();
    }
  }
}
