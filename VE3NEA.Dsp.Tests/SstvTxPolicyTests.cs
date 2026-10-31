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
      new(2026, 10, 31, 1, 2, 3, DateTimeKind.Utc);

    private static SstvTxGuard Valid() =>
      SstvTxPolicy.Validate("sat", "tx", false, true, true, true,
        Slicer.Mode.USB, 435000000, 0, 435000000, true, false,
        false, true, Now);

    [Fact]
    public void AcceptsExplicitlyApprovedAboveHorizonUplink()
    {
      SstvTxGuard guard = Valid();
      Assert.Equal(435000000, guard.ExpectedCatHz);
      Assert.Equal(Slicer.Mode.USB, guard.Mode);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void DeniesAbsentApprovalOrBadSatelliteState(
      bool approved, bool hasUplink, bool observation, bool horizon)
    {
      Assert.Throws<InvalidOperationException>(() =>
        SstvTxPolicy.Validate("sat", "tx", false, hasUplink,
          observation, horizon, Slicer.Mode.USB, 435000000, 0,
          435000000, true, false, false, approved, Now));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(435005000L)]
    [InlineData(434999500L)]
    public void DeniesUnverifiedOrWrongActualCatFrequency(long actual)
    {
      Assert.Throws<InvalidOperationException>(() =>
        SstvTxPolicy.Validate("sat", "tx", false, true, true, true,
          Slicer.Mode.USB, 435000000, 0, actual, true, false,
          false, true, Now));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void DeniesAnyExistingKeyedOrReservedTransmitter(
      bool ptt, bool conflict)
    {
      Assert.Throws<InvalidOperationException>(() =>
        SstvTxPolicy.Validate("sat", "tx", false, true, true, true,
          Slicer.Mode.USB, 435000000, 0, 435000000, true, ptt,
          conflict, true, Now));
    }

    [Fact]
    public void DopplerUpdatesAllowedButWrongTxOrStalePttAreRejected()
    {
      var guard = Valid();
      Assert.True(SstvTxPolicy.StillSafe(guard, "sat", "tx", true,
        true, 435001000, 0, 435001000, true,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
      Assert.False(SstvTxPolicy.StillSafe(guard, "sat", "other-tx", true,
        true, 435001000, 0, 435001000, true,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
      Assert.False(SstvTxPolicy.StillSafe(guard, "sat", "tx", true,
        true, 435001000, 0, 435001300, true,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
      Assert.False(SstvTxPolicy.StillSafe(guard, "sat", "tx", true,
        true, 435001000, 0, 435001000, true, Now,
        Now.AddSeconds(3)));
      Assert.False(SstvTxPolicy.StillSafe(guard, "sat", "tx", true,
        false, 435001000, 0, 435001000, true,
        Now.AddMilliseconds(700), Now.AddSeconds(1)));
    }

    [Fact]
    public void TransverterCatOffsetIsPartOfArmedAndCurrentFrequency()
    {
      var guard = SstvTxPolicy.Validate("sat", "tx", false, true,
        true, true, Slicer.Mode.USB, 1296000000, 1160000000,
        136000000, true, false, false, true, Now);
      Assert.True(SstvTxPolicy.StillSafe(guard, "sat", "tx", true,
        true, 1296000550, 1160000000, 136000550, true,
        Now.AddMilliseconds(500), Now.AddSeconds(1)));
    }
  }
}
