using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Low-quality but narrow-uncertainty FM estimates must be observed for
  /// several distinct measurement epochs before becoming a control reference.
  /// This is temporal corroboration, NOT statistically independent samples:
  /// neighbouring overlapped FFT windows can share IF data.
  /// </summary>
  public sealed class AfcFmEvidenceGate
  {
    private DateTime previousUtc;
    private double previousHz;
    private int count;

    public int Consecutive => count;

    public void Reset()
    {
      previousUtc = default;
      previousHz = 0;
      count = 0;
    }

    public bool Observe(AfcObservation observation, bool enabled = true)
    {
      if (!enabled || observation.Mode != AfcSignalMode.FM ||
          !observation.HasAbsoluteReference || observation.Ambiguous ||
          !double.IsFinite(observation.IfOffsetHz) ||
          !double.IsFinite(observation.Confidence) ||
          !double.IsFinite(observation.SigmaHz) ||
          observation.Confidence < .22 ||
          observation.SigmaHz > 45 ||
          Math.Abs(observation.IfOffsetHz) > 6800 ||
          observation.SampleUtc.Kind != DateTimeKind.Utc)
      {
        Reset();
        return false;
      }

      if (observation.Confidence >= .60)
      {
        // Strong-quality FM goes through the existing standard gate;
        // it never needs to wait for additional low-quality corroboration.
        Reset();
        return false;
      }

      if (previousUtc == default)
      {
        previousUtc = observation.SampleUtc;
        previousHz = observation.IfOffsetHz;
        count = 1;
        return false;
      }

      double dt = (observation.SampleUtc - previousUtc).TotalSeconds;
      if (dt < .08)
        return false; // repeated/overlapped callback with no new time evidence
      double delta = Math.Abs(observation.IfOffsetHz - previousHz);
      double tolerance = Math.Max(55, 2.5 * observation.SigmaHz);
      if (dt > .7 || delta > tolerance)
      {
        // A new carrier or a long silent gap must reacquire independently.
        previousUtc = observation.SampleUtc;
        previousHz = observation.IfOffsetHz;
        count = 1;
        return false;
      }
      previousUtc = observation.SampleUtc;
      previousHz = observation.IfOffsetHz;
      count = Math.Min(4, count + 1);
      return count >= 3;
    }
  }
}
