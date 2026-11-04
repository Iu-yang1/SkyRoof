using System;

namespace SkyRoof.AFC
{
  public sealed partial class AfcController
  {
    private void StepServo(AfcObservation obs, AfcObservationContext context,
      double deltaTime, int sign, double error)
    {
      double maxCorrectionHz = context.MaxCorrectionHz;
      double fmDeadbandHz = context.FmDeadbandHz;
      double toneDeadbandHz = context.ToneDeadbandHz;
      bool transmitting = context.Transmitting;
      bool calibrationInProgress = context.CalibrationInProgress;
      bool mayWrite = state.Control == AfcControlMode.Auto &&
        confirmations >= 2 && sign != 0 && !transmitting &&
        !calibrationInProgress && !waitForManualSign &&
        obs.SampleUtc >= recoveryUntilUtc;
      if (obs.Mode == AfcSignalMode.SSB && !obs.HasAbsoluteReference)
        mayWrite = false;
      if (mayWrite)
      {
        double deadband = obs.Mode == AfcSignalMode.FM
          ? fmDeadbandHz : toneDeadbandHz;
        if (Math.Abs(error) >= deadband)
        {
          double step = Math.Clamp(sign * error * 0.55,
            -Math.Min(120, 250 * deltaTime),
            Math.Min(120, 250 * deltaTime));
          acceptedCorrection = Math.Clamp(acceptedCorrection + step,
            -maxCorrectionHz, maxCorrectionHz);
        }
      }
    }

  }
}
