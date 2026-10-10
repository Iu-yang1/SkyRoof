using FluentAssertions;
using NAudio.Wave;
using SkyRoof;
using SkyRoof.CW;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class CwAudioSourceControllerTests
  {
    private static readonly DateTime T0 =
      new(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CwWasapiCapture_CanUseSmallAudioPacketsWithoutChangingLegacyDefault()
    {
      using var cwCapture = new InputSoundcard<float>(
        captureBufferMilliseconds: 40,
        readerBlockSizeSamples: 1600);
      using var otherCapture = new InputSoundcard<float>();
      cwCapture.CaptureBufferMilliseconds.Should().Be(40);
      cwCapture.ReaderBlockSizeSamples.Should().Be(1600);
      otherCapture.CaptureBufferMilliseconds.Should().Be(200);
      otherCapture.ReaderBlockSizeSamples.Should().Be(4800);
    }

    [Theory]
    [InlineData(0, 1600)]
    [InlineData(19, 1600)]
    [InlineData(1001, 1600)]
    [InlineData(40, 0)]
    [InlineData(40, 127)]
    public void WasapiCapture_InvalidLatencyOrBlockSizeIsRejected(
      int latency, int blockSamples)
    {
      Action create = () =>
      {
        using var device = new InputSoundcard<float>(
          captureBufferMilliseconds: latency,
          readerBlockSizeSamples: blockSamples);
      };
      create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ingress_PcmDeliveryTelemetryResetsOnSourceChange()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(true, CwReceiveAudioSource.WasapiCapture, "mic");
      float[] block = new float[1600];
      ingress.Append(
        CwReceiveAudioSource.WasapiCapture, block, block.Length, T0)
        .Should().BeTrue();
      ingress.Append(
        CwReceiveAudioSource.WasapiCapture, block, block.Length,
        T0.AddMilliseconds(33)).Should().BeTrue();
      ingress.AcceptedBlocks.Should().Be(2);
      ingress.LastBlockSamples.Should().Be(1600);
      ingress.LastPcmDeliveryIntervalMs.Should().BeGreaterOrEqualTo(0);
      ingress.Configure(false, CwReceiveAudioSource.WasapiCapture, "mic");
      ingress.AcceptedBlocks.Should().Be(0);
      ingress.LastBlockSamples.Should().Be(0);
      ingress.LastPcmDeliveryIntervalMs.Should().Be(0);
    }

    [Fact]
    public void Ingress_SignalsOnlyAfterFreshAcceptedPcm()
    {
      var ingress = new CwPcmIngress();
      int signals = 0;
      ingress.SamplesAccepted += () =>
      {
        ingress.FrontEnd.Audio.TotalSamplesWritten.Should()
          .BeGreaterThan(0);
        signals++;
      };
      float[] samples = new float[1600];

      ingress.Configure(true, CwReceiveAudioSource.WasapiCapture, "mic");
      ingress.Append(CwReceiveAudioSource.SDR,
        samples, 1600, T0).Should().BeFalse();
      signals.Should().Be(0);
      ingress.Append(CwReceiveAudioSource.WasapiCapture,
        samples, 0, T0).Should().BeFalse();
      signals.Should().Be(0);

      ingress.Append(CwReceiveAudioSource.WasapiCapture,
        samples, 1600, T0).Should().BeTrue();
      ingress.Append(CwReceiveAudioSource.WasapiCapture,
        samples, 1600, T0.AddMilliseconds(30)).Should().BeTrue();
      signals.Should().Be(2);

      ingress.Configure(false, CwReceiveAudioSource.WasapiCapture, "mic");
      ingress.Append(CwReceiveAudioSource.WasapiCapture,
        samples, 1600, T0.AddMilliseconds(60)).Should().BeFalse();
      signals.Should().Be(2);
    }

    [Fact]
    public void Ingress_AcceptsOnlyConfiguredSource()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(
        enabled: true,
        CwReceiveAudioSource.SDR);

      float[] block = new float[4800];

      ingress.Append(
          CwReceiveAudioSource.WasapiCapture,
          block,
          block.Length,
          T0)
        .Should().BeFalse();

      ingress.Append(
          CwReceiveAudioSource.SDR,
          block,
          block.Length,
          T0)
        .Should().BeTrue();

      ingress.AcceptedSamples.Should().Be(4800);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(4800);
    }

    [Fact]
    public void Ingress_DisabledSourceNeverAccumulatesPcm()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(
        enabled: false,
        CwReceiveAudioSource.WasapiCapture);

      float[] block = new float[9600];
      ingress.Append(
          CwReceiveAudioSource.WasapiCapture,
          block,
          block.Length,
          T0)
        .Should().BeFalse();

      ingress.AcceptedSamples.Should().Be(0);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(0);
    }

    [Fact]
    public void Ingress_SourceSwitchClearsPreviousAudioTimeline()
    {
      var ingress = new CwPcmIngress();
      float[] sdr = Enumerable.Repeat(
        0.25f, 4800).ToArray();
      float[] loopback = Enumerable.Repeat(
        -0.5f, 2400).ToArray();

      ingress.Configure(
        true,
        CwReceiveAudioSource.SDR);
      ingress.Append(
        CwReceiveAudioSource.SDR,
        sdr,
        sdr.Length,
        T0);

      ingress.Configure(
        true,
        CwReceiveAudioSource.RsBa1Loopback);

      ingress.AcceptedSamples.Should().Be(0);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(0);

      ingress.Append(
        CwReceiveAudioSource.RsBa1Loopback,
        loopback,
        loopback.Length,
        T0.AddSeconds(1));

      ingress.AcceptedSamples.Should().Be(2400);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(2400);
    }

    [Fact]
    public void Ingress_SameSourceDifferentDeviceStartsNewGeneration()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(
        true,
        CwReceiveAudioSource.WasapiCapture,
        "capture-A");

      float[] first = new float[4800];
      ingress.Append(
        CwReceiveAudioSource.WasapiCapture,
        first,
        first.Length,
        T0);

      long generation =
        ingress.TimelineGeneration;

      ingress.Configure(
        true,
        CwReceiveAudioSource.WasapiCapture,
        "capture-B");

      ingress.TimelineGeneration
        .Should().Be(generation + 1);
      ingress.AcceptedSamples.Should().Be(0);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(0);
      ingress.SourceIdentity.Should().Be("capture-B");
    }

    [Fact]
    public void Ingress_BackwardUtcResetsTimelineAndKeepsNewBlock()
    {
      var ingress = new CwPcmIngress();
      ingress.Configure(
        true,
        CwReceiveAudioSource.SDR);

      float[] first = new float[4800];
      float[] second = new float[2400];

      ingress.Append(
        CwReceiveAudioSource.SDR,
        first,
        first.Length,
        T0.AddSeconds(2));

      ingress.Append(
          CwReceiveAudioSource.SDR,
          second,
          second.Length,
          T0.AddSeconds(1))
        .Should().BeTrue();

      ingress.AcceptedSamples.Should().Be(2400);
      ingress.FrontEnd.Audio.TotalSamplesWritten
        .Should().Be(2400);
      ingress.LastAcceptedUtc
        .Should().Be(T0.AddSeconds(1));
    }

    [Theory]
    [InlineData(CwReceiveAudioSource.SDR, "SkyRoof SDR audio", "internal 48 kHz AF")]
    [InlineData(CwReceiveAudioSource.WasapiCapture, "IC-9700 USB AF input", "must be AF, not IF")]
    [InlineData(CwReceiveAudioSource.RsBa1Loopback, "RS-BA1 playback loopback", "endpoint RS-BA1 plays into")]
    public void AudioSourceLabelsExplainTheExpectedSignalPath(
      CwReceiveAudioSource source,
      string displayName,
      string roleFragment)
    {
      CwAudioSourceController
        .SourceDisplayName(source)
        .Should().Be(displayName);

      CwAudioSourceController
        .SourceRoleHint(source)
        .Should().Contain(roleFragment);
    }

    [Fact]
    public void RsBa1LoopbackDevice_ExplicitCwSelectionWins()
    {
      var cw = new CwConsoleSettings
      {
        RsBa1LoopbackDeviceId = "cw-render"
      };
      var audio = new AudioSettings
      {
        RsBa1PlaybackDeviceId = "remote-utility-render"
      };

      CwAudioSourceController
        .ResolveLoopbackDeviceId(cw, audio)
        .Should().Be("cw-render");
    }

    [Fact]
    public void RsBa1LoopbackDevice_FallsBackToRemoteUtilityEndpoint()
    {
      var cw = new CwConsoleSettings
      {
        RsBa1LoopbackDeviceId = null
      };
      var audio = new AudioSettings
      {
        RsBa1PlaybackDeviceId = "remote-utility-render"
      };

      CwAudioSourceController
        .ResolveLoopbackDeviceId(cw, audio)
        .Should().Be("remote-utility-render");
    }

    [Fact]
    public void MonoMixSampleProvider_AveragesEveryInputChannel()
    {
      var source = new ArraySampleProvider(
        WaveFormat.CreateIeeeFloatWaveFormat(
          48000,
          3),
        [
          0.3f, 0.6f, 0.9f,
          -0.3f, 0.0f, 0.3f
        ]);

      var mono =
        new MonoMixSampleProvider(source);
      float[] output = new float[4];

      int read =
        mono.Read(
          output,
          0,
          output.Length);

      read.Should().Be(2);
      output[0].Should().BeApproximately(
        0.6f,
        1e-6f);
      output[1].Should().BeApproximately(
        0.0f,
        1e-6f);
      mono.WaveFormat.Channels.Should().Be(1);
      mono.WaveFormat.SampleRate.Should().Be(48000);
    }

    private sealed class ArraySampleProvider :
      ISampleProvider
    {
      private readonly float[] samples;
      private int position;

      public WaveFormat WaveFormat { get; }

      public ArraySampleProvider(
        WaveFormat waveFormat,
        float[] samples)
      {
        WaveFormat = waveFormat;
        this.samples = samples;
      }

      public int Read(
        float[] buffer,
        int offset,
        int count)
      {
        int available =
          Math.Min(
            count,
            samples.Length - position);
        if (available <= 0)
          return 0;

        Array.Copy(
          samples,
          position,
          buffer,
          offset,
          available);
        position += available;
        return available;
      }
    }
  }
}
