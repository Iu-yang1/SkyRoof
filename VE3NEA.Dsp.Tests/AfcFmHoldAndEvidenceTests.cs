using System;
using FluentAssertions;
using SkyRoof;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcFmHoldAndEvidenceTests
  {
    private static readonly DateTime T0 =
      new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);

    private static AfcController LockedFm()
    {
      var c = new AfcController();
      c.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 6; i++)
        c.Observe(new(AfcSignalMode.FM, T0.AddMilliseconds(150 * i),
            82, 280, 9, .93, true),
          436795000, 1, 2000, 20, 5, false);
      c.Snapshot.CorrectionHz.Should().BeGreaterThan(30);
      return c;
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(90)]
    public void DefaultSignalLossHoldsLastTrustedAfcBiasForFmGaps(
      int seconds)
    {
      var c = LockedFm();
      double before = c.Snapshot.CorrectionHz;
      c.Tick(T0.AddSeconds(seconds));
      c.Tick(T0.AddSeconds(seconds + .3));
      c.Snapshot.CorrectionHz.Should().Be(before,
        "missing FM speech must not erase residual tuning");
      c.GetRxCorrection(false).Should().Be(before);
      c.Snapshot.State.Should().BeOneOf(AfcLockState.Coasting,
        AfcLockState.Hold, AfcLockState.Lost);
    }

    [Fact]
    public void IrregularNoiseFramesNeverEraseAccumulatedFmCorrection()
    {
      var c = LockedFm();
      double bias = c.Snapshot.CorrectionHz;
      for (int i = 0; i < 120; i++)
      {
        var when = T0.AddSeconds(1.5 + i * .2);
        c.Observe(new(AfcSignalMode.FM, when, 82,
            4200 - 37 * (i % 23), 980, .01, false),
          436795000, 1, 2000, 20, 5, false);
        c.Tick(when);
      }
      c.Snapshot.CorrectionHz.Should().Be(bias,
        "short false carrier detections are not grounds to reset AFC");
    }

    [Fact]
    public void OptionalGentleReleaseWaitsThirtySecondsAndIsRateLimited()
    {
      var c = LockedFm();
      c.ConfigureSignalLoss(AfcSignalLossPolicy.GentleRelease);
      double before = c.Snapshot.CorrectionHz;
      c.Tick(T0.AddSeconds(20));
      c.Snapshot.CorrectionHz.Should().Be(before);
      c.Tick(T0.AddSeconds(31));
      c.Tick(T0.AddSeconds(32));
      c.Snapshot.CorrectionHz.Should().BeLessThan(before)
        .And.BeGreaterThanOrEqualTo(before - 20.01);
    }

    [Fact]
    public void PttFreezesOptionalReleaseEvenAfterThirtySeconds()
    {
      var c = LockedFm();
      c.ConfigureSignalLoss(AfcSignalLossPolicy.GentleRelease);
      double before = c.Snapshot.CorrectionHz;
      c.Tick(T0.AddSeconds(35), transmitting: true);
      c.Tick(T0.AddSeconds(36), transmitting: true);
      c.Snapshot.CorrectionHz.Should().Be(before);
    }

    [Fact]
    public void ValidNewFmAfterLongGapReacquiresWithoutZeroingRxBias()
    {
      var c = LockedFm();
      double before = c.Snapshot.CorrectionHz;
      c.Tick(T0.AddSeconds(12));
      c.Observe(new(AfcSignalMode.FM, T0.AddSeconds(13),
            82, 305, 8, .93, true),
        436795000, 1, 2000, 20, 5, false);
      c.Snapshot.State.Should().NotBe(AfcLockState.Fault);
      c.Snapshot.CorrectionHz.Should().BeGreaterThan(0);
      c.Snapshot.CorrectionHz.Should().BeInRange(before - 100, before + 100);
    }

    [Fact]
    public void WeakFmMustHaveThreeDistinctConsistentEpochs()
    {
      var gate = new AfcFmEvidenceGate();
      AfcObservation Weak(int ms, double hz) =>
        new(AfcSignalMode.FM, T0.AddMilliseconds(ms),
          14, hz, 17, .26, true);
      gate.Observe(Weak(0, -694)).Should().BeFalse();
      gate.Observe(Weak(100, -685)).Should().BeFalse();
      gate.Observe(Weak(210, -690)).Should().BeTrue();
      gate.Consecutive.Should().Be(3);
    }

    [Fact]
    public void WeakFmNoiseAndFrequencyHopsNeverTriggerReference()
    {
      var gate = new AfcFmEvidenceGate();
      gate.Observe(new(AfcSignalMode.FM, T0, 14, -690, 17, .26, true))
        .Should().BeFalse();
      gate.Observe(new(AfcSignalMode.FM, T0.AddMilliseconds(100),
            14, 1200, 17, .26, true)).Should().BeFalse();
      gate.Observe(new(AfcSignalMode.FM, T0.AddMilliseconds(200),
            14, -690, 17, .26, true)).Should().BeFalse();
      gate.Observe(new(AfcSignalMode.FM, T0.AddMilliseconds(300),
            14, -689, 185, .26, true)).Should().BeFalse();
      gate.Observe(new(AfcSignalMode.FM, T0.AddMilliseconds(400),
            14, -692, 17, .20, true)).Should().BeFalse();
    }

    [Fact]
    public void WeakThreeEpochFmControlCanLockWithoutLoweringOtherModes()
    {
      var c = new AfcController();
      c.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      for (int i = 0; i < 6; i++)
        c.Observe(new(AfcSignalMode.FM,
            T0.AddMilliseconds(120 * i), 21,
            -694 + (i % 2) * 3, 17, .26, true),
          436795000, 1, 2000, 20, 5, false);
      c.Snapshot.CorrectionHz.Should().BeLessThan(-15,
        "validated repeated weak FM measurements should enable AFC");
      c.Snapshot.State.Should().Be(AfcLockState.Locked);
      var cw = new AfcController();
      cw.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      for (int i = 0; i < 7; i++)
        cw.Observe(new(AfcSignalMode.CW,
            T0.AddMilliseconds(i * 120), 21, 600, 17, .26, true),
          436795000, 1, 2000, 20, 5, false);
      cw.Snapshot.CorrectionHz.Should().Be(0,
        "weak FM exception cannot lower CW confidence requirements");
    }
  }
}
