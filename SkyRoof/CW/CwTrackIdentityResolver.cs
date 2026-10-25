using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  internal readonly record struct CwIdentityResolution(
    IReadOnlyList<CwSignalTrack> Tracks,
    IReadOnlyList<int> RetiredTrackIds);

  /// <summary>
  /// A conservative alias reconciliation stage after the multi-hypothesis
  /// associator/Kalman tracker and BEFORE the lanes enter DeepCW.
  ///
  /// A close frequency by itself is not an identity: independent CW stations
  /// may be spaced less than one fast-FFT bin apart. Retire a duplicate only
  /// when its keyed time envelope agrees, its frequency/rate agrees AND the
  /// precision observations do not resolve two simultaneous ridges.
  /// </summary>
  internal static class CwTrackIdentityResolver
  {
    internal const double MaximumAliasSeparationHz = 6.0;
    internal const double MaximumAliasRateDifferenceHzPerSecond = 12.0;
    private const double MinimumEnvelopeCorrelation = 0.96;

    internal static CwIdentityResolution Resolve(
      IReadOnlyList<CwSignalTrack> tracks,
      CwFrameRidgeScanResult scan,
      int sampleRate,
      long snapshotEndSampleIndex,
      int fastHopSamples)
    {
      ArgumentNullException.ThrowIfNull(tracks);
      ArgumentNullException.ThrowIfNull(scan);
      if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
      if (fastHopSamples <= 0) throw new ArgumentOutOfRangeException(nameof(fastHopSamples));

      // Keep the active, older labelled identity where possible, so an
      // ephemeral re-birth does not replace a confirmed transcript/slot.
      var prioritized = tracks
        .OrderByDescending(t => t.Active)
        .ThenByDescending(t => t.Confirmed)
        .ThenByDescending(t => t.KeyingStatus == CwKeyingStatus.Verified)
        .ThenBy(t => t.FirstSeenUtc)
        .ThenByDescending(t => t.SnrDb)
        .ThenBy(t => t.Id)
        .ToArray();

      var kept = new List<CwSignalTrack>(prioritized.Length);
      var retired = new List<int>();
      foreach (CwSignalTrack track in prioritized)
      {
        bool alias = kept.Any(existing => SamePhysicalCarrier(
          existing, track, scan, sampleRate, snapshotEndSampleIndex,
          fastHopSamples));
        if (alias) retired.Add(track.Id);
        else kept.Add(track);
      }

      return new(
        kept.OrderBy(t => t.FrequencyHz).ToArray(),
        retired);
    }

    private static bool SamePhysicalCarrier(
      CwSignalTrack a,
      CwSignalTrack b,
      CwFrameRidgeScanResult scan,
      int sampleRate,
      long endSample,
      int fastHopSamples)
    {
      if (!double.IsFinite(a.FrequencyHz) ||
          !double.IsFinite(b.FrequencyHz) ||
          !double.IsFinite(a.DriftHzPerSecond) ||
          !double.IsFinite(b.DriftHzPerSecond) ||
          Math.Abs(a.FrequencyHz - b.FrequencyHz) >
            MaximumAliasSeparationHz ||
          Math.Abs(a.DriftHzPerSecond - b.DriftHzPerSecond) >
            MaximumAliasRateDifferenceHzPerSecond)
        return false;

      // Preserve distinct, independently resolved precision peaks even when
      // their envelopes happen to be identical (e.g. synchronized sending).
      if (HasIndependentPrecisionRidges(a, b, scan.PrecisionBatches,
        sampleRate, endSample))
        return false;

      if (a.AssociationHintId != 0 &&
          a.AssociationHintId == b.AssociationHintId)
        return true;

      // A tracking coincidence is insufficient. Both lanes must have been
      // physically recognized as keyed CW before envelope-based aliasing.
      if (a.KeyingStatus != CwKeyingStatus.Verified ||
          b.KeyingStatus != CwKeyingStatus.Verified)
        return false;

      return CorrelatedFastKeying(a, b, scan.FastObservations,
        sampleRate, endSample, fastHopSamples);
    }

    private static bool HasIndependentPrecisionRidges(
      CwSignalTrack a,
      CwSignalTrack b,
      IReadOnlyList<CwRidgeObservationBatch> batches,
      int sampleRate,
      long endSample)
    {
      int resolved = 0;
      foreach (CwRidgeObservationBatch batch in batches)
      {
        double time = (batch.CenterSampleIndex - endSample) /
          (double)sampleRate;
        double expectedA = a.FrequencyHz + a.DriftHzPerSecond * time;
        double expectedB = b.FrequencyHz + b.DriftHzPerSecond * time;
        // Only two independently observed local maxima, not the same peak
        // assigned twice, constitute evidence of two RF carriers.
        CwRidgeObservation[] nearby = batch.Observations
          .Where(x => x.KalmanEligible &&
            (Math.Abs(x.FrequencyHz - expectedA) <= 7.0 ||
             Math.Abs(x.FrequencyHz - expectedB) <= 7.0))
          .ToArray();
        bool distinct = false;
        for (int i = 0; i < nearby.Length && !distinct; i++)
          for (int j = i + 1; j < nearby.Length; j++)
          {
            if (Math.Abs(nearby[i].FrequencyHz -
                  nearby[j].FrequencyHz) < 3.5)
              continue;
            distinct =
              (Math.Abs(nearby[i].FrequencyHz - expectedA) <= 5.0 &&
               Math.Abs(nearby[j].FrequencyHz - expectedB) <= 5.0) ||
              (Math.Abs(nearby[j].FrequencyHz - expectedA) <= 5.0 &&
               Math.Abs(nearby[i].FrequencyHz - expectedB) <= 5.0);
            if (distinct) break;
          }

        if (distinct && ++resolved >= 2)
          return true;
      }
      return false;
    }

    private static bool CorrelatedFastKeying(
      CwSignalTrack a,
      CwSignalTrack b,
      IReadOnlyList<CwRidgeObservation> observations,
      int sampleRate,
      long endSample,
      int fastHopSamples)
    {
      // A shared source should have the SAME ON/OFF shape over time. Use
      // all fast frame times including missing peaks (key-up); using only
      // frames where both are ON would inflate unrelated correlations.
      if (observations.Count == 0) return false;
      long first = observations.Min(x => x.CenterSampleIndex);
      long last = observations.Max(x => x.CenterSampleIndex);
      long frames = (last - first) / fastHopSamples + 1;
      if (frames < 24 || frames > 5000) return false;
      long[] times = Enumerable.Range(0, (int)frames)
        .Select(i => first + (long)i * fastHopSamples)
        .ToArray();

      var aByFrame = BestSnr(a, observations, sampleRate, endSample);
      var bByFrame = BestSnr(b, observations, sampleRate, endSample);
      double[] x = times.Select(t => aByFrame.GetValueOrDefault(t, -6.0)).ToArray();
      double[] y = times.Select(t => bByFrame.GetValueOrDefault(t, -6.0)).ToArray();

      double meanX = x.Average(), meanY = y.Average();
      double cov = 0, varX = 0, varY = 0;
      for (int i = 0; i < times.Length; i++)
      {
        double dx = x[i] - meanX, dy = y[i] - meanY;
        cov += dx * dy;
        varX += dx * dx;
        varY += dy * dy;
      }

      // Flat carriers, broadband noise and weakly modulated AGC artifacts
      // must not be merged on nearly undefined Pearson correlation.
      if (varX / times.Length < 9 || varY / times.Length < 9)
        return false;
      return cov / Math.Sqrt(varX * varY) >=
        MinimumEnvelopeCorrelation;
    }

    private static Dictionary<long, double> BestSnr(
      CwSignalTrack track,
      IReadOnlyList<CwRidgeObservation> observations,
      int sampleRate,
      long endSample)
    {
      var result = new Dictionary<long, (double Distance, double Snr)>();
      foreach (CwRidgeObservation observation in observations)
      {
        double dt = (observation.CenterSampleIndex - endSample) /
          (double)sampleRate;
        double expected = track.FrequencyHz +
          track.DriftHzPerSecond * dt;
        double distance = Math.Abs(observation.FrequencyHz - expected);
        if (distance > 8.0) continue;
        if (!result.TryGetValue(observation.CenterSampleIndex,
              out var previous) ||
            distance < previous.Distance ||
            (distance == previous.Distance &&
             observation.SnrDb > previous.Snr))
          result[observation.CenterSampleIndex] =
            (distance, observation.SnrDb);
      }
      return result.ToDictionary(x => x.Key, x => x.Value.Snr);
    }

    /// <summary>
    /// A second, defensive test for inference callers that bypass the
    /// live tracker. Require highly similar NON-FLAT activity masks.
    /// </summary>
    internal static bool SameActivityAlias(
      CwSignalTrack a,
      CwSignalTrack b,
      ReadOnlySpan<float> activityA,
      ReadOnlySpan<float> activityB)
    {
      if (a.Id == b.Id) return false;
      if (Math.Abs(a.FrequencyHz - b.FrequencyHz) >
            MaximumAliasSeparationHz ||
          Math.Abs(a.DriftHzPerSecond - b.DriftHzPerSecond) >
            MaximumAliasRateDifferenceHzPerSecond)
        return false;
      if (a.AssociationHintId != 0 &&
          a.AssociationHintId == b.AssociationHintId)
        return true;
      // Inference masks have no access to independent precision peak
      // evidence. Two simultaneously active, separately labelled stations
      // must stay separate here; only the full frontend can merge them.
      if (a.Active && b.Active) return false;
      if (activityA.Length != activityB.Length ||
          activityA.Length < 24)
        return false;

      double meanA = 0, meanB = 0;
      for (int i = 0; i < activityA.Length; i++)
      {
        meanA += activityA[i];
        meanB += activityB[i];
      }
      meanA /= activityA.Length;
      meanB /= activityB.Length;
      double covariance = 0, varianceA = 0, varianceB = 0;
      for (int i = 0; i < activityA.Length; i++)
      {
        double da = activityA[i] - meanA;
        double db = activityB[i] - meanB;
        covariance += da * db;
        varianceA += da * da;
        varianceB += db * db;
      }
      if (varianceA / activityA.Length < 0.01 ||
          varianceB / activityB.Length < 0.01)
        return false;
      return covariance / Math.Sqrt(varianceA * varianceB) >= 0.985;
    }

    internal static IReadOnlyList<CwSignalTrack> DeduplicateInferenceLanes(
      IReadOnlyList<CwSignalTrack> tracks,
      IReadOnlyDictionary<int, float[]> activities)
    {
      var selected = new List<CwSignalTrack>(tracks.Count);
      foreach (CwSignalTrack track in tracks
        .OrderByDescending(x => x.Active)
        .ThenByDescending(x => x.IdentityConfidence)
        .ThenBy(x => x.FirstSeenUtc)
        .ThenByDescending(x => x.SnrDb))
      {
        if (!activities.TryGetValue(track.Id, out float[]? activity))
          continue;
        bool alias = selected.Any(existing =>
          activities.TryGetValue(existing.Id, out float[]? other) &&
          SameActivityAlias(existing, track, other, activity));
        if (!alias) selected.Add(track);
      }
      return selected.OrderBy(x => x.FrequencyHz).ToArray();
    }
  }
}
