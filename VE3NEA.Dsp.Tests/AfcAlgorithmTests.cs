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
      var result = estimator.Analyze(pcm, AfcSignalMode.CW, T0, 1, expectedOffsetHz:145);
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

    [Fact]
    public void FM_Modulated12kHzIf_RecoversNominalCenter()
    {
      const int fs = 48000;
      float[] pcm = new float[fs];
      for (int i = 0; i < pcm.Length; i++)
      {
        double t = i / (double)fs;
        double phase = 2 * Math.PI * (12000 + 145) * t +
          1.6 * Math.Sin(2 * Math.PI * 300 * t);
        pcm[i] = (float)(0.35 * Math.Cos(phase));
      }
      var e = new AfcSignalProcessing();
      var obs = e.Analyze(pcm, AfcSignalMode.FM, T0, 1);
      obs.HasAbsoluteReference.Should().BeTrue();
      obs.IfOffsetHz.Should().BeApproximately(145, 30);
    }

    [Fact]
    public void CW_LocalTargetGate_IgnoresDistantStrongerCarrier()
    {
      const int fs = 48000;
      float[] pcm = new float[fs];
      for (int i = 0; i < fs; i++)
      {
        double t = i / (double)fs;
        pcm[i] = (float)(
          0.22 * Math.Cos(2 * Math.PI * 12105 * t) +
          0.44 * Math.Cos(2 * Math.PI * 12430 * t));
      }
      var e = new AfcSignalProcessing();
      var obs = e.Analyze(pcm, AfcSignalMode.CW, T0, 1,
        expectedOffsetHz:105);
      obs.HasAbsoluteReference.Should().BeTrue();
      obs.IfOffsetHz.Should().BeApproximately(105, 5);
    }

    [Fact]
    public void AFC_StaleReferenceLeavesLockedState()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 4; i++)
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddMilliseconds(i * 600), 1, i == 0 ? 0 : 45,
          1, 1, true),
          145000000, 1, 500, 20, 5, false);
      controller.Snapshot.State.Should().Be(AfcLockState.Locked);
      controller.Tick(T0.AddSeconds(12));
      controller.Snapshot.State.Should().Be(AfcLockState.Lost);
    }

    [Fact]
    public void AFC_ManualTuneDiscardsOldAutoCorrection()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 4; i++)
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddMilliseconds(i * 600), 1, i == 0 ? 0 : 45,
          1, 1, true),
          145000000, 1, 500, 20, 5, false);
      controller.Snapshot.CorrectionHz.Should().BeGreaterThan(0);
      controller.OnUserTune();
      controller.Snapshot.CorrectionHz.Should().Be(0);
    }

    [Fact]
    public void SSB_RelativeEstimateNeverEnablesAbsoluteAfc()
    {
      const int fs = 48000;
      var estimator = new AfcSignalProcessing();
      for (int run = 0; run < 2; run++)
      {
        var pcm = new float[fs];
        for (int i = 0; i < fs; i++)
        {
          double t = i / (double)fs;
          pcm[i] = (float)(.25 * Math.Cos(2 * Math.PI * (12400 + 12 * run) * t) +
            .15 * Math.Cos(2 * Math.PI * (12950 + 12 * run) * t));
        }
        var obs = estimator.Analyze(pcm, AfcSignalMode.SSB,
          T0.AddSeconds(run), 1, ssbPilot:false);
        obs.HasAbsoluteReference.Should().BeFalse();
      }
    }


    [Fact]
    public void FM_AutoCentersInitialOffsetRatherThanLockingItInPlace()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 4; i++)
        controller.Observe(new(AfcSignalMode.FM,
          T0.AddMilliseconds(600 * i), 1, 140, 8, 1, true),
          145000000, 1, 2000, 20, 5, false);
      controller.Snapshot.CorrectionHz.Should().BeGreaterThan(0);
      controller.Snapshot.ErrorHz.Should().BeApproximately(140, 1);
    }

    [Fact]
    public void AFC_LearnsSignOnlyFromRepeatedConsistentCatIfMotion()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 6; i++)
        controller.Observe(new(AfcSignalMode.FM,
          T0.AddMilliseconds(600 * i), 1, 200 - 40 * i, 5, 1, true),
          145000000 + 40 * i, 0, 2000, 20, 5, false);
      controller.Snapshot.MappingSign.Should().Be(1);
    }


    [Fact]
    public void AFC_ManualDialMotionCanTeachPolarityWithoutLosingEvidence()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Assist);
      controller.Observe(new(AfcSignalMode.FM, T0, 4, 200, 3, 1, true),
        145000000, 0, 2000, 20, 5, false);
      for (int k = 1; k <= 4; k++)
      {
        controller.OnUserTune();
        controller.Observe(new(AfcSignalMode.FM,
          T0.AddSeconds(k), 4, 200 - 40 * k, 3, 1, true),
          145000000 + 40 * k, 0, 2000, 20, 5, false);
      }
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      controller.Observe(new(AfcSignalMode.FM,
        T0.AddSeconds(5), 4, 140, 3, 1, true),
        145000160, 0, 2000, 20, 5, false);
      controller.Observe(new(AfcSignalMode.FM,
        T0.AddSeconds(6), 4, 140, 3, 1, true),
        145000160, 0, 2000, 20, 5, false);
      controller.Snapshot.MappingSign.Should().Be(1);
    }


    [Fact]
    public void CatTimeline_UsesReadbackAtIFSampleTimeNotLaterFrequency()
    {
      var history = new AfcCatTimeline();
      history.RecordReadback(145000000, T0);
      history.RecordReadback(145000150, T0.AddMilliseconds(250));
      history.RecordReadback(145000500, T0.AddMilliseconds(900));
      history.TryReadbackAt(T0.AddMilliseconds(300), out long selected)
        .Should().BeTrue();
      selected.Should().Be(145000150);
      history.TryReadbackAt(T0.AddSeconds(3), out _)
        .Should().BeFalse("a stale CAT read must not teach IF polarity");
      history.TryReadbackAt(T0.AddMilliseconds(-50), out _)
        .Should().BeFalse("future readbacks are not historical evidence");
    }

    [Fact]
    public void CatTimeline_TracksWriteSourcesSeparatelyFromReadback()
    {
      var history = new AfcCatTimeline();
      history.RecordRequest(145000100, T0, AfcCatWriteSource.Afc);
      history.ExpectedWriteSource(145000100, T0.AddSeconds(1))
        .Should().Be(AfcCatWriteSource.Afc);
      history.ExpectedWriteSource(145000180, T0.AddSeconds(1))
        .Should().BeNull();
      history.ExpectedWriteSource(145000100, T0.AddSeconds(7))
        .Should().BeNull();
      history.TryReadbackAt(T0.AddMilliseconds(200), out _)
        .Should().BeFalse("a requested frequency is not a CAT readback ACK");
    }

    [Fact]
    public void CW_DesiredCenterCanDifferFromSelectedSearchFrequency()
    {
      const int fs = 48000;
      var pcm = new float[fs];
      for (int i = 0; i < fs; i++)
        pcm[i] = (float)(.35 * Math.Cos(2 * Math.PI *
          12145 * i / fs));
      var estimator = new AfcSignalProcessing();
      var result = estimator.Analyze(pcm, AfcSignalMode.CW, T0, 5,
        expectedOffsetHz: 145);
      result.HasAbsoluteReference.Should().BeTrue();
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      controller.CenterOn(0);
      for (int k = 0; k < 4; k++)
        controller.Observe(result with {
          SampleUtc = T0.AddMilliseconds(k * 600)
        }, 145000000, 1, 500, 20, 5, false);
      controller.Snapshot.ErrorHz.Should().BeApproximately(145, 5);
      controller.Snapshot.CorrectionHz.Should().BeGreaterThan(0);
    }

    [Fact]
    public void CW_TargetFollowsSlowIFMotionWithinOneGeneration()
    {
      const int fs = 48000;
      var estimator = new AfcSignalProcessing();
      for (int k = 0; k <= 5; k++)
      {
        int hz = 12000 + 145 - 35 * k;
        var pcm = new float[fs];
        for (int i = 0; i < fs; i++)
          pcm[i] = (float)(.35 * Math.Cos(2 * Math.PI *
            hz * i / fs));
        var obs = estimator.Analyze(pcm, AfcSignalMode.CW,
          T0.AddSeconds(k), 6, expectedOffsetHz:145);
        obs.HasAbsoluteReference.Should().BeTrue();
        obs.IfOffsetHz.Should().BeApproximately(145 - 35*k, 5);
      }
    }

    [Fact]
    public void FM_ConfidenceIsIndependentOfReasonableWindowsGain()
    {
      const int fs = 48000;
      var high = new float[fs];
      var low = new float[fs];
      for (int i = 0; i < fs; i++)
      {
        double t = i / (double)fs;
        double phase = 2 * Math.PI * (12000 + 85) * t +
          1.1 * Math.Sin(2 * Math.PI * 310 * t);
        high[i] = (float)(.5 * Math.Cos(phase));
        low[i] = high[i] / 10;
      }
      var a = new AfcSignalProcessing().Analyze(high, AfcSignalMode.FM, T0, 1);
      var b = new AfcSignalProcessing().Analyze(low, AfcSignalMode.FM, T0, 1);
      Math.Abs(a.Confidence - b.Confidence).Should().BeLessThan(.1);
    }

    [Fact]
    public void Controller_ReconfiguresSameModeAfterSatelliteReset()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      controller.Reset("new FM satellite");
      controller.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      controller.Snapshot.Control.Should().Be(AfcControlMode.Auto);
      controller.Snapshot.State.Should().Be(AfcLockState.Acquiring);
    }


    [Fact]
    public void Controller_ConfirmedDivergentAfcReadbacksStopAutomaticWrites()
    {
      var controller = new AfcController();
      controller.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      controller.CenterOn(0);
      int[] increasinglyWrong = { 100, 160, 260, 420, 680, 800 };
      for (int k = 0; k < increasinglyWrong.Length; k++)
      {
        controller.Observe(new(AfcSignalMode.CW,
          T0.AddSeconds(k), 42, increasinglyWrong[k], 1, 1, true),
          145000000 + 50 * k, 1, 2000, 20, 5, false,
          catReadbackIsTimeAligned: true,
          confirmedOwnAfcReadback: true);
      }
      controller.Snapshot.State.Should().Be(AfcLockState.Fault);
      controller.Tick(T0.AddMinutes(1));
      controller.Snapshot.State.Should().Be(AfcLockState.Fault,
        "the background timer must not silently clear a convergence fault");
      double frozen = controller.Snapshot.CorrectionHz;
      controller.Observe(new(AfcSignalMode.CW,
        T0.AddSeconds(10), 42, 1400, 1, 1, true),
        145000400, 1, 2000, 20, 5, false,
        catReadbackIsTimeAligned: true, confirmedOwnAfcReadback: true);
      controller.Snapshot.CorrectionHz.Should().Be(frozen);
      controller.LockHere();
      controller.Snapshot.State.Should().Be(AfcLockState.Acquiring);
    }

  }
}
