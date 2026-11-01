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

    private static AudioDeviceEntry Device(string id, string name) => new(id, name);

    [Fact]
    public void AutoDiscoverPairsOnlyMatchingVbCableRenderAndCapture()
    {
      var result = IfAfSignedCableDiscovery.Select(
        new[] { Device("radioTx", "Speakers (USB Audio CODEC)"),
                Device("vbOut", "CABLE Input (VB-Audio Virtual Cable)") },
        new[] { Device("vbIn", "CABLE Output (VB-Audio Virtual Cable)"),
                Device("radioRx", "Microphone (USB Audio CODEC)") });
      Assert.True(result.Ready);
      Assert.Equal("vbOut", result.RenderId);
      Assert.Equal("vbIn", result.CaptureId);
      Assert.Equal("VB-CABLE", result.Provider);
    }

    [Fact]
    public void AutoDiscoverFallsBackToVacLiteWithoutDefaultSpeakers()
    {
      var result = IfAfSignedCableDiscovery.Select(
        new[] { Device("lineOut", "Line 1 (Virtual Audio Cable)"),
                Device("realSpeakers", "Speakers (Realtek Audio)") },
        new[] { Device("lineIn", "Line 1 (Virtual Audio Cable)") });
      Assert.True(result.Ready);
      Assert.Equal("lineOut", result.RenderId);
      Assert.Equal("lineIn", result.CaptureId);
      Assert.Equal("VAC Lite", result.Provider);
    }

    [Fact]
    public void AutoDiscoverRejectsMissingOrAmbiguousPairs()
    {
      var output = Device("vbOut", "CABLE Input (VB-Audio Virtual Cable)");
      var input = Device("vbIn", "CABLE Output (VB-Audio Virtual Cable)");
      Assert.False(IfAfSignedCableDiscovery.Select(new[] { output },
        Array.Empty<AudioDeviceEntry>()).Ready);
      Assert.False(IfAfSignedCableDiscovery.Select(new[] { output, output },
        new[] { input }).Ready);
      Assert.False(IfAfSignedCableDiscovery.Select(
        new[] { Device("test", "Speakers (USB Audio CODEC)") },
        new[] { input }).Ready);
      Assert.False(IfAfSignedCableDiscovery.Select(
        new[] { Device("both", "CABLE Input (VB-Audio Virtual Cable)") },
        new[] { Device("both", "CABLE Output (VB-Audio Virtual Cable)") }).Ready);
    }

    [Theory]
    [InlineData("CABLE Input (VB-Audio Virtual Cable)", true)]
    [InlineData("CABLE Input", true)]
    [InlineData("CABLE Output (VB-Audio Virtual Cable)", false)]
    [InlineData("USB Audio CODEC", false)]
    [InlineData("CABLE Input (Some Other Vendor)", false)]
    public void DetectVbCableEndpointNames(string name, bool expected)
    {
      Assert.Equal(expected, IfAfSignedCableDiscovery.IsVbCableEnd(name, "CABLE Input"));
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
    public void DefaultRoutePrefersVendorCableAndKeepsManualFallback()
    {
      var cfg = new IfAfSettings();
      Assert.False(cfg.Enabled);
      Assert.Equal(IfAfAudioRoute.AutoSignedCable, cfg.AudioRoute);
      Assert.Equal(1, (int)IfAfAudioRoute.ExistingVirtualCable);
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
