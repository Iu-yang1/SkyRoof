using CSCore;
using FluentAssertions;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public class AudioCompatibilityTests
  {
    [Fact]
    public void VendoredWaveSource_LoadsAgainstModernCSCore()
    {
      AssemblyName().Name.Should().Be("CSCore");
      AssemblyName().Version.Should().Be(new Version(1, 2, 1, 2));

      using var source = new global::VE3NEA.WaveSource<float>(48_000);
      IWaveSource waveSource = source;

      waveSource.WaveFormat.SampleRate.Should().Be(48_000);
      waveSource.WaveFormat.Channels.Should().Be(1);
      waveSource.WaveFormat.BitsPerSample.Should().Be(32);

      source.AddSamples(new[] { 0.25f, -0.5f });

      byte[] buffer = new byte[16];
      waveSource.Read(buffer, 0, buffer.Length).Should().Be(buffer.Length);

      BitConverter.ToSingle(buffer, 0).Should().Be(0.25f);
      BitConverter.ToSingle(buffer, 4).Should().Be(-0.5f);
      BitConverter.ToSingle(buffer, 8).Should().Be(0f);
      BitConverter.ToSingle(buffer, 12).Should().Be(0f);
    }

    private static System.Reflection.AssemblyName AssemblyName()
    {
      return typeof(IWaveSource).Assembly.GetName();
    }
  }
}
