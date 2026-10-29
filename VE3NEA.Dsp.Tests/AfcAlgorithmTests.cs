using System;
using FluentAssertions;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcAlgorithmTests
  {
    private static readonly DateTime T0 =
      new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CW_Real12kHzIf_PeakEstimatesInjectedOffset()
    {
      const int fs = 48000;
      var pcm = new float[fs];
      for (int i = 0; i < pcm.Length; i++)
        pcm[i] = (float)(0.3 * Math.Cos(2 * Math.PI * 12145 * i / fs));
      var estimator = new AfcSignalProcessing();
      var result = estimator.Analyze(pcm, AfcSignalMode.CW, T0, 1);
      result.HasAbsoluteReference.Should().BeTrue();
      result.IfOffsetHz.Should().BeApproximately(145, 3);
    }

    [Fact]
    public void SSB_WithoutPilotNeverClaimsAbsoluteFrequency()
    {
      var pcm = new float[48000];
      var estimator = new AfcSignalProcessing();
      var result = estimator.Analyze(pcm, AfcSignalMode.SSB, T0, 1, ssbPilot:false);
      result.HasAbsoluteReference.Should().BeFalse();
    }

    [Fact]
    public void AFC_NeverControlsWithoutVerifiedFrequencyPolarity()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 5; i++)
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddMilliseconds(500*i), 1, i == 0 ? 0 : 55, 1, 1, true),
          145000000, 0, 500, 20, 5, false);
      controller.Snapshot.CorrectionHz.Should().Be(0);
      controller.Snapshot.MappingSign.Should().Be(0);
    }

    [Fact]
    public void AFC_HoldsOnLostReferenceAndFreezesOnPtt()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 4; i++)
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddMilliseconds(500*i), 1, i == 0 ? 0 : 60, 1, 1, true),
          145000000, 1, 500, 20, 5, false);
      double before = controller.Snapshot.CorrectionHz;
      before.Should().BeGreaterThan(0);
      controller.Observe(new(AfcSignalMode.CW,
        T0.AddSeconds(3), 1, 800, 999, 0, false),
        145000000, 1, 500, 20, 5, false);
      controller.Snapshot.State.Should().Be(AfcLockState.Hold);
      controller.Snapshot.CorrectionHz.Should().Be(before);
      controller.Observe(new(AfcSignalMode.CW,
        T0.AddSeconds(4), 1, 900, 1, 1, true),
        145000000, 1, 500, 20, 5, true);
      controller.Snapshot.CorrectionHz.Should().Be(before);
    }

    [Fact]
    public void AFC_SampleGenerationSwitchResetsCorrection()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 4; i++)
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddMilliseconds(500*i), 1, i==0 ? 0 : 80, 1, 1, true),
          145000000, 1, 500, 20, 5, false);
      controller.Snapshot.CorrectionHz.Should().BeGreaterThan(0);
      controller.Observe(new(AfcSignalMode.CW,
        T0.AddSeconds(3), 2, 0, 1, 1, true),
        145000000, 1, 500, 20, 5, false);
      controller.Snapshot.CorrectionHz.Should().Be(0);
    }
  }
}
