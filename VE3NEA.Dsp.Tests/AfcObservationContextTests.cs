using System;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcObservationContextTests
  {
    [Fact]
    public void ImmutableContextProducesIdenticalServoStatesToLegacyApi()
    {
      var oldApi = new AfcController();
      var contextual = new AfcController();
      oldApi.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      contextual.Configure(AfcSignalMode.FM, AfcControlMode.Auto);
      DateTime t0 = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
      for (int k = 0; k < 9; k++)
      {
        double freq = 160 - k * 12;
        var obs = new AfcObservation(AfcSignalMode.FM,
          t0.AddMilliseconds(650 * k), 8, freq, 8, .95, true);
        oldApi.Observe(obs, 145000000, 1, 2000, 20, 5, false,
          catReadbackIsTimeAligned: true, confirmedOwnAfcReadback: false,
          calibrationInProgress: false, nominalSign: 1);
        contextual.Observe(obs, new AfcObservationContext(
          145000000, 1, 2000, 20, 5, false,
          CatReadbackIsTimeAligned: true, ConfirmedOwnAfcReadback: false,
          CalibrationInProgress: false, NominalSign: 1));
        Assert.Equal(oldApi.Snapshot, contextual.Snapshot);
      }
    }

    [Fact]
    public void NewContextPreservesTxInterlockAndTimeAlignment()
    {
      var oldApi = new AfcController();
      var contextual = new AfcController();
      oldApi.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      contextual.Configure(AfcSignalMode.CW, AfcControlMode.Auto);
      DateTime t0 = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);
      for (int k = 0; k < 6; k++)
      {
        var obs = new AfcObservation(AfcSignalMode.CW,
          t0.AddMilliseconds(600 * k), 4, 90 - k, 3, .93, true);
        bool tx = k >= 4;
        oldApi.Observe(obs, 145000000, 1, 500, 20, 5, tx,
          catReadbackIsTimeAligned: k != 3);
        contextual.Observe(obs, new AfcObservationContext(
          145000000, 1, 500, 20, 5, tx,
          CatReadbackIsTimeAligned: k != 3));
        Assert.Equal(oldApi.Snapshot, contextual.Snapshot);
      }
    }
  }
}
