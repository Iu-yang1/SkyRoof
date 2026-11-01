using System;
using System.ComponentModel;
using System.IO;
using SkyRoof.IF;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfAudioLinkTests
  {
    [Theory]
    [InlineData("IF-AF-Audio Playback", true)]
    [InlineData("Speakers (IF-AF-Audio Playback)", true)]
    [InlineData("扬声器 (IF-AF-Audio Playback)", true)]
    [InlineData("扬声器（IF-AF-Audio Playback）", true)]
    [InlineData("扬声器 (Fake IF-AF-Audio Playback)", false)]
    [InlineData("Speakers (USB Audio CODEC)", false)]
    [InlineData("ICOM_VAUDIO-1 (Icom Virtual Audio Device)", false)]
    [InlineData("IF-AF-Audio", false)]
    public void PlaybackRouteOnlyRecognizesDedicatedVirtualPlayback(
      string name, bool valid) =>
      Assert.Equal(valid, IfAfAudioEndpointDiscovery.IsExpectedRenderName(name));

    [Theory]
    [InlineData("IF-AF-Audio", true)]
    [InlineData("Microphone (IF-AF-Audio)", true)]
    [InlineData("麦克风 (IF-AF-Audio)", true)]
    [InlineData("麦克风（IF-AF-Audio）", true)]
    [InlineData("麦克风 (Fake IF-AF-Audio)", false)]
    [InlineData("IF-AF-Audio Playback", false)]
    [InlineData("Microphone (USB Audio CODEC)", false)]
    public void InputRouteOnlyRecognizesDedicatedVirtualCapture(
      string name, bool valid) =>
      Assert.Equal(valid, IfAfAudioEndpointDiscovery.IsExpectedCaptureName(name));

    [Fact]
    public void VirtualDeviceNeedsBothEndpoints()
    {
      Assert.False(new IfAfEndpointPair("one", null, "missing").Ready);
      Assert.False(new IfAfEndpointPair(null, "two", "missing").Ready);
      Assert.True(new IfAfEndpointPair("one", "two", "both").Ready);
    }

    [Fact]
    public void OnlyCompleteDeveloperPackageIsRecognized()
    {
      var folder = Path.Combine(Path.GetTempPath(),
        "if-af-audio-package-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(folder);
      try
      {
        Assert.False(IfAfAudioEndpointDiscovery.IsCompleteDevelopmentPackage(folder));
        File.WriteAllBytes(Path.Combine(folder, "IfAfAudio.sys"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(folder, "IfAfAudio.inf"), "test");
        Assert.False(IfAfAudioEndpointDiscovery.IsCompleteDevelopmentPackage(folder));
        File.WriteAllBytes(Path.Combine(folder, "ifafaudio.cat"), new byte[] { 1 });
        Assert.True(IfAfAudioEndpointDiscovery.IsCompleteDevelopmentPackage(folder));
      }
      finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void BackwardCompatibleVacRouteAndLowLatencyDefaults()
    {
      var cfg = new IfAfSettings();
      Assert.False(cfg.Enabled);
      Assert.Equal(IfAfAudioRoute.ExistingVirtualCable, cfg.AudioRoute);
      Assert.Equal(IfAfLatencyPreset.Balanced40Ms, cfg.LatencyPreset);
      var info = TypeDescriptor.GetProperties(typeof(IfAfSettings))["DriverStatus"];
      Assert.NotNull(info);
      Assert.IsType<ReadOnlyAttribute>(info.Attributes[typeof(ReadOnlyAttribute)]);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void WasapiRenderUsesRequestedBufferPeriod(int milliseconds)
    {
      using var sink = new OutputSoundcard<float>(
        samplingRate: 48000, latencyMilliseconds: milliseconds);
      Assert.Equal(milliseconds, sink.RequestedLatencyMs);
    }

    [Fact]
    public void InvalidRenderLatencyIsRejected()
    {
      Assert.Throws<ArgumentOutOfRangeException>(() =>
        new OutputSoundcard<float>(
          samplingRate: 48000, latencyMilliseconds: 5));
    }

    [Fact]
    public void LowLatencyIfReaderDefaultsRemainBackwardCompatible()
    {
      using var traditional = new InputSoundcard<float>(
        samplingRate: 48000);
      Assert.Equal(200, traditional.CaptureBufferMilliseconds);
      Assert.Equal(4800, traditional.ReaderBlockSizeSamples);
      using var fast = new InputSoundcard<float>(
        samplingRate: 48000, captureBufferMilliseconds: 20,
        readerBlockSizeSamples: 480, idleSleepMilliseconds: 2);
      Assert.Equal(20, fast.CaptureBufferMilliseconds);
      Assert.Equal(480, fast.ReaderBlockSizeSamples);
    }
  }
}
