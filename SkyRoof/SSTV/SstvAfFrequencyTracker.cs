using System;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// A frequency observation from a separate, validated VIS/sync detector.
  /// ToneOffsetHz is the offset of a known SSTV AF tone from its nominal pitch.
  /// Raw IF samples, arbitrary audio spectrum peaks, and CAT deltas are NOT valid input.
  /// Epoch must change whenever the selected signal path, transmitter, or demodulation
  /// identity changes.
  /// </summary>
  public readonly record struct SstvAfFrequencyObservation(
    DateTime TimestampUtc,
    long Epoch,
    double ToneOffsetHz,
    double Confidence);

  /// <summary>
  /// Advisory AF compensation only. CorrectionHz is suitable for a FUTURE analytic
  /// AF frequency translator, not for direct mono multiplication or CAT/PTT writes.
  /// </summary>
  public readonly record struct SstvAfFrequencySnapshot(
    bool Locked,
    long Epoch,
    double EstimatedOffsetHz,
    double RateHzPerSecond,
    double CorrectionHz,
    int AcceptedObservations,
    DateTime LastObservationUtc);

  /// <summary>
  /// A bounded alpha-beta residual-frequency estimator for SSTV AF sync tones.
  ///
  /// This component intentionally has no radio, soundcard, or NCO dependencies.
  /// It cannot transmit, move CAT, or alter saved satellite Doppler offsets.
  /// </summary>
  public sealed class SstvAfFrequencyTracker
  {
    public const double MinimumConfidence = 0.60;
    public const double MaxAbsoluteOffsetHz = 2000.0;
    public const double MaxRateHzPerSecond = 25.0;
    public const double MaxInnovationHz = 160.0;
    public const double MaxObservationGapSeconds = 3.0;
    public const int RequiredObservations = 3;

    private readonly object sync = new();
    private bool hasEpoch;
    private long epoch;
    private int accepted;
    private DateTime lastTimestampUtc;
    private double estimatedOffsetHz;
    private double estimatedRateHzPerSecond;

    public void Reset()
    {
      lock (sync)
        Clear();
    }

    /// <summary>
    /// Returns true only when the new observation is accepted. Invalid data and
    /// isolated jumps do not modify filter state. A new epoch discards the old
    /// estimate before acquiring a fresh measurement.
    /// </summary>
    public bool TryObserve(SstvAfFrequencyObservation observation)
    {
      if (observation.TimestampUtc.Kind != DateTimeKind.Utc ||
          !double.IsFinite(observation.ToneOffsetHz) ||
          !double.IsFinite(observation.Confidence) ||
          observation.Confidence < MinimumConfidence ||
          observation.Confidence > 1.0 ||
          Math.Abs(observation.ToneOffsetHz) > MaxAbsoluteOffsetHz)
        return false;

      lock (sync)
      {
        if (!hasEpoch || epoch != observation.Epoch)
        {
          Clear();
          epoch = observation.Epoch;
          hasEpoch = true;
        }

        if (accepted > 0)
        {
          double dt = (observation.TimestampUtc - lastTimestampUtc).TotalSeconds;
          if (dt <= 0)
            return false;

          // Never carry an old satellite pass or an interrupted audio stream
          // forward as a current, locked frequency estimate.
          if (dt > MaxObservationGapSeconds)
          {
            Clear();
            epoch = observation.Epoch;
            hasEpoch = true;
          }
          else
          {
            double predicted = estimatedOffsetHz + estimatedRateHzPerSecond * dt;
            double innovation = observation.ToneOffsetHz - predicted;

            if (Math.Abs(innovation) > MaxInnovationHz)
              return false;

            double alpha = 0.35 * observation.Confidence;
            double beta = 0.08 * observation.Confidence;
            estimatedOffsetHz = Math.Clamp(
              predicted + alpha * innovation,
              -MaxAbsoluteOffsetHz,
              MaxAbsoluteOffsetHz);
            estimatedRateHzPerSecond = Math.Clamp(
              estimatedRateHzPerSecond + beta * innovation / dt,
              -MaxRateHzPerSecond,
              MaxRateHzPerSecond);

            accepted = Math.Min(accepted + 1, int.MaxValue);
            lastTimestampUtc = observation.TimestampUtc;
            return true;
          }
        }

        estimatedOffsetHz = observation.ToneOffsetHz;
        estimatedRateHzPerSecond = 0.0;
        accepted = 1;
        lastTimestampUtc = observation.TimestampUtc;
        return true;
      }
    }

    /// <summary>
    /// Freshness is checked at the caller's UTC time. An old locked estimate
    /// cannot authorize downstream audio compensation or CAT activity.
    /// </summary>
    public SstvAfFrequencySnapshot GetSnapshot(DateTime nowUtc)
    {
      if (nowUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("An explicit UTC time is required.", nameof(nowUtc));

      lock (sync)
      {
        bool fresh = accepted > 0 &&
          nowUtc >= lastTimestampUtc &&
          (nowUtc - lastTimestampUtc).TotalSeconds <= MaxObservationGapSeconds;
        bool locked = fresh && accepted >= RequiredObservations;
        return new SstvAfFrequencySnapshot(
          locked,
          epoch,
          estimatedOffsetHz,
          estimatedRateHzPerSecond,
          locked ? -estimatedOffsetHz : 0.0,
          accepted,
          lastTimestampUtc);
      }
    }

    private void Clear()
    {
      hasEpoch = false;
      epoch = 0;
      accepted = 0;
      lastTimestampUtc = default;
      estimatedOffsetHz = 0;
      estimatedRateHzPerSecond = 0;
    }
  }
}
