using System;
using SkyRoof.AFC;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class AfcPolarityIndependentSampleTests
  {
    private static readonly DateTime Origin =
      new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ReplayedIfSampleCannotCountAsSecondIndependentProbeOrRestore()
    {
      var calibrator = new AfcPolarityCalibrator();
      void Observe(double second, long catHz, double ifHz, bool ack,
        bool eligible = true)
      {
        calibrator.Observe(
          new AfcObservation(AfcSignalMode.FM, Origin.AddSeconds(second),
            501, ifHz, 4, .95, true), catHz, ack, eligible, 10,
          Origin.AddSeconds(second + 1));
      }

      Observe(0, 145000000, 100, false);
      Observe(.6, 145000000, 100, false);
      Observe(1.2, 145000000, 100, false);
      Assert.Equal(AfcPolarityCalibrator.Phase.Probing, calibrator.State);

      Observe(3.6, 145000150, -50, true);
      for (int i = 0; i < 5; i++)
        Observe(3.6, 145000150, -50, true);
      Assert.Equal(AfcPolarityCalibrator.Phase.Probing, calibrator.State);
      Assert.Equal(0, calibrator.VerifiedSign);

      Observe(4.2, 145000150, -50, true);
      Assert.Equal(AfcPolarityCalibrator.Phase.Restoring, calibrator.State);
      Observe(6.6, 145000000, 100, true);
      for (int i = 0; i < 5; i++)
        Observe(6.6, 145000000, 100, true);
      Assert.Equal(AfcPolarityCalibrator.Phase.Restoring, calibrator.State);
      Assert.Equal(0, calibrator.VerifiedSign);

      Observe(7.2, 145000000, 100, true);
      Assert.Equal(AfcPolarityCalibrator.Phase.Complete, calibrator.State);
      Assert.Equal(1, calibrator.VerifiedSign);
    }

    [Fact]
    public void SafetyEligibilityLossStillCancelsDuplicateSampleDuringProbe()
    {
      var calibrator = new AfcPolarityCalibrator();
      for (int i = 0; i < 3; i++)
        calibrator.Observe(new AfcObservation(AfcSignalMode.FM,
          Origin.AddMilliseconds(600 * i), 501, 100, 4, .95, true),
          145000000, false, true, 10,
          Origin.AddMilliseconds(600 * i + 1000));
      Assert.Equal(AfcPolarityCalibrator.Phase.Probing, calibrator.State);

      calibrator.Observe(new AfcObservation(AfcSignalMode.FM,
        Origin.AddMilliseconds(1200), 501, 100, 4, .95, true),
        145000000, false, false, 10, Origin.AddSeconds(3));
      Assert.Equal(AfcPolarityCalibrator.Phase.Failed, calibrator.State);
    }
  }
}
