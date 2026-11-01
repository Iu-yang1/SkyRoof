using System;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Bounded alpha-beta estimator for a transient FM satellite repeater.
  /// Tracks residual 12-kHz IF center and its slow drift between validated
  /// carrier bursts. No new measurement is invented during squelch/noise.
  /// Neither this predictor nor its extrapolation directly authorizes CAT.
  /// </summary>
  public sealed class AfcFmPredictor
  {
    public const double MaximumRateHzPerSecond = 90;
    public const double MaximumCoastSeconds = 1.5;
    public DateTime LastValidUtc { get; private set; }
    public double OffsetHz { get; private set; }
    public double RateHzPerSecond { get; private set; }
    public bool HasEstimate => LastValidUtc != default;

    public void Reset()
    {
      LastValidUtc = default;
      OffsetHz = RateHzPerSecond = 0;
    }

    public bool TryPredict(DateTime utc, out double offsetHz,
      double maximumAgeSeconds = MaximumCoastSeconds)
    {
      offsetHz = 0;
      if (!HasEstimate || utc < LastValidUtc ||
          maximumAgeSeconds < 0 || (utc - LastValidUtc).TotalSeconds > maximumAgeSeconds)
        return false;
      double dt = (utc - LastValidUtc).TotalSeconds;
      offsetHz = OffsetHz + RateHzPerSecond * dt;
      return double.IsFinite(offsetHz);
    }

    public bool Update(double measuredHz, double sigmaHz,
      double confidence, DateTime sampleUtc)
    {
      if (!double.IsFinite(measuredHz) || Math.Abs(measuredHz) > 8000 ||
          !double.IsFinite(sigmaHz) || sigmaHz > 150 ||
          !double.IsFinite(confidence) || confidence < .6 ||
          sampleUtc.Kind != DateTimeKind.Utc ||
          (HasEstimate && sampleUtc <= LastValidUtc))
        return false;

      if (!HasEstimate || (sampleUtc - LastValidUtc).TotalSeconds > MaximumCoastSeconds)
      {
        OffsetHz = measuredHz;
        RateHzPerSecond = 0;
        LastValidUtc = sampleUtc;
        return true;
      }

      double dt = (sampleUtc - LastValidUtc).TotalSeconds;
      double projected = OffsetHz + RateHzPerSecond * dt;
      double innovation = measuredHz - projected;
      // Outliers > 500 Hz need independent future corroboration. A burst
      // appearing far away after squelch must not drag a locked carrier.
      if (Math.Abs(innovation) > 500)
        return false;

      double quality = Math.Clamp(confidence * 50.0 /
        Math.Max(10.0, sigmaHz), .30, 1);
      double alpha = .60 * quality;
      double beta = .085 * quality;
      OffsetHz = projected + alpha * innovation;
      RateHzPerSecond = Math.Clamp(
        RateHzPerSecond + beta * innovation / Math.Max(.08, dt),
        -MaximumRateHzPerSecond, MaximumRateHzPerSecond);
      LastValidUtc = sampleUtc;
      return true;
    }
  }
}
