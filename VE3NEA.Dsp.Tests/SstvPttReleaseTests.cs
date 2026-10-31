using System;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvPttReleaseTests
  {
    private static readonly DateTime Started =
      new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void RequiresFreshOffHardwareObservationAfterApplicationKeyed()
    {
      Assert.True(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));

      // OFF command acknowledgement alone is not a CAT RX-state read.
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddSeconds(-1),
        Started, Started.AddSeconds(1)));

      // A fresh readback of ON, or unresolved owned PTT, must fail closed.
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        true, false, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, true, Started.AddMilliseconds(400),
        Started, Started.AddMilliseconds(500)));
    }

    [Fact]
    public void RejectsStaleFutureOrNonUtcReadback()
    {
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started,
        Started, Started.AddMilliseconds(1600)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false, Started.AddSeconds(3),
        Started, Started.AddMilliseconds(500)));
      Assert.False(SstvTxPolicy.HasConfirmedPttOff(
        false, false,
        DateTime.SpecifyKind(Started, DateTimeKind.Local),
        Started, Started.AddMilliseconds(500)));
    }
  }
}
