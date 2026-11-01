using System;
using SkyRoof;
using SkyRoof.SSTV;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvTxPolicyTests
  {
    private static readonly DateTime Now =
      new(2026, 11, 1, 1, 2, 3, DateTimeKind.Utc);

    private static SstvTxGuard Arm(bool canPtt = true,
      bool alreadyTransmitting = false, bool leaseBusy = false) =>
      SstvTxPolicy.Validate(null, null, true, false, false, false,
        Slicer.Mode.CW, 0, 0, 0, canPtt,
        alreadyTransmitting, leaseBusy, false, Now);

    [Fact]
    public void ExplicitTxDoesNotForceSatelliteModeOrFrequency()
    {
      var guard = Arm();
      Assert.Equal(0, guard.ExpectedCatHz);
      Assert.Equal(Slicer.Mode.CW, guard.Mode);
      Assert.Equal(Now, guard.StartedUtc);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void RejectsMissingPttCapabilityOrConflictingTxLease(
      bool canPtt, bool keyed, bool busy)
    {
      Assert.Throws<InvalidOperationException>(() =>
        Arm(canPtt, keyed, busy));
    }

    [Fact]
    public void ExistingHardwareFrequencyIsRecordedButNotForced()
    {
      var guard = SstvTxPolicy.Validate(null, null, true, false,
        false, false, Slicer.Mode.LSB, 0, 0,
        145825000, true, false, false, false, Now);
      Assert.Equal(145825000, guard.ExpectedCatHz);
    }

    [Fact]
    public void LiveTxRequiresFreshHardwarePttReadbackNotSatelliteMatch()
    {
      var guard = Arm();
      Assert.True(SstvTxPolicy.StillSafe(guard, "different", "tx",
        false, false, 0, 0, 0, true,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
      Assert.False(SstvTxPolicy.StillSafe(guard, null, null,
        true, true, 435000000, 0, 435000000, false,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
      Assert.False(SstvTxPolicy.StillSafe(guard, null, null,
        true, true, 435000000, 0, 435000000, true,
        Now, Now.AddSeconds(3)));
      Assert.False(SstvTxPolicy.StillSafe(guard, null, null,
        true, true, 435000000, 0, 435000000, true,
        Now.AddSeconds(2), Now.AddSeconds(1)));
    }

    [Fact]
    public void PttOffMustBeIndependentlyObservedAfterRelease()
    {
      Assert.True(SstvTxPolicy.HasConfirmedPttOff(false, false,
        Now.AddMilliseconds(150), Now, Now.AddMilliseconds(300)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(true, false,
        Now.AddMilliseconds(150), Now, Now.AddMilliseconds(300)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(false, true,
        Now.AddMilliseconds(150), Now, Now.AddMilliseconds(300)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(false, false,
        Now.AddMilliseconds(-150), Now, Now.AddMilliseconds(300)));
    }
  }
}
