using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  /// <summary>A peak measured by a CW detector, in audio-frequency Hz.</summary>
  public readonly record struct CwSignalCandidate(double FrequencyHz, double SnrDb);

  /// <summary>
  /// Stable identity and observed state of one independently decodable CW lane.
  /// Frequency/velocity are the posterior state at the current tracker time.
  /// </summary>
  public readonly record struct CwSignalTrack(
    int Id,
    double FrequencyHz,
    double SnrDb,
    double DriftHzPerSecond,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    bool Confirmed,
    bool Active,
    bool Ambiguous = false,
    double FrequencySigmaHz = 0);

  /// <summary>
  /// Multi-target CW ridge tracker.
  ///
  /// Each CW component is modelled as a constant-velocity target in the
  /// time-frequency plane: x=[frequency, frequency-rate]. A small Kalman
  /// filter predicts through Doppler drift and short QSB dropouts. All
  /// candidate/track assignments are solved globally per scan (bounded exact
  /// min-cost assignment for <=8 tracks), rather than by greedy nearest peak.
  ///
  /// When two tracks temporarily collapse into one spectral measurement, one
  /// track may be updated and the other coasts on prediction until they become
  /// separable again. This is intentionally closer to multi-target tracking
  /// with merged measurements than to independent peak chasing.
  /// </summary>
  public sealed class CwPileupTrackManager
  {
    private sealed class State
    {
      public int Id;
      public double FrequencyHz;
      public double DriftHzPerSecond;
      public double P00 = 100;
      public double P01;
      public double P11 = 400;
      public double SnrDb;
      public DateTime FirstSeenUtc;
      public DateTime LastSeenUtc;
      public DateTime StateUtc;
      public int SeenCount;
      public bool Active;
      public bool Ambiguous;
    }

    private readonly List<State> tracks = new();
    private DateTime? previousUpdateUtc;
    private int nextId = 1;

    public int MaxTracks { get; }
    public double MinimumSeparationHz { get; }
    public double MatchToleranceHz { get; }
    public TimeSpan ConfirmationDelay { get; }
    public TimeSpan HoldTime { get; }

    /// <summary>1-sigma random acceleration of a ridge, Hz/s².</summary>
    public double ProcessAccelerationSigma { get; }

    /// <summary>Base 1-sigma frequency measurement error at high SNR, Hz.</summary>
    public double BaseMeasurementSigmaHz { get; }

    /// <summary>
    /// Separation below which two predicted ridges are flagged ambiguous.
    /// They remain separate tracks; the separator can surface the uncertainty.
    /// </summary>
    public double MergeResolutionHz { get; }

    public CwPileupTrackManager(
      int maxTracks = 8,
      double minimumSeparationHz = 25,
      double matchToleranceHz = 40,
      TimeSpan? confirmationDelay = null,
      TimeSpan? holdTime = null,
      double processAccelerationSigma = 8,
      double baseMeasurementSigmaHz = 3,
      double mergeResolutionHz = 18)
    {
      if (maxTracks is < 1 or > 32)
        throw new ArgumentOutOfRangeException(nameof(maxTracks));
      if (!double.IsFinite(minimumSeparationHz) || minimumSeparationHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(minimumSeparationHz));
      if (!double.IsFinite(matchToleranceHz) || matchToleranceHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(matchToleranceHz));
      if (!double.IsFinite(processAccelerationSigma) || processAccelerationSigma <= 0)
        throw new ArgumentOutOfRangeException(nameof(processAccelerationSigma));
      if (!double.IsFinite(baseMeasurementSigmaHz) || baseMeasurementSigmaHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(baseMeasurementSigmaHz));
      if (!double.IsFinite(mergeResolutionHz) || mergeResolutionHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(mergeResolutionHz));

      MaxTracks = maxTracks;
      MinimumSeparationHz = minimumSeparationHz;
      MatchToleranceHz = matchToleranceHz;
      ConfirmationDelay = confirmationDelay ?? TimeSpan.FromMilliseconds(400);
      HoldTime = holdTime ?? TimeSpan.FromSeconds(5);
      ProcessAccelerationSigma = processAccelerationSigma;
      BaseMeasurementSigmaHz = baseMeasurementSigmaHz;
      MergeResolutionHz = mergeResolutionHz;

      if (ConfirmationDelay < TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(confirmationDelay));
      if (HoldTime <= TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(holdTime));
    }

    public void Reset()
    {
      tracks.Clear();
      previousUpdateUtc = null;
      nextId = 1;
    }

    public IReadOnlyList<CwSignalTrack> Update(
      DateTime utc,
      IEnumerable<CwSignalCandidate> candidates)
    {
      ArgumentNullException.ThrowIfNull(candidates);
      if (utc.Kind != DateTimeKind.Utc)
        throw new ArgumentException("CW tracking requires UTC timestamps.", nameof(utc));
      if (previousUpdateUtc is DateTime previous && utc < previous)
        throw new ArgumentException("CW detector timestamps must be monotonic.", nameof(utc));

      previousUpdateUtc = utc;
      tracks.RemoveAll(t => utc - t.LastSeenUtc > HoldTime);

      var clean = SuppressDuplicateCandidates(candidates);

      // Predict every live ridge to this scan before doing any association.
      foreach (State track in tracks)
      {
        Predict(track, utc);
        track.Active = false;
        track.Ambiguous = false;
      }

      // Exact global min-cost association is inexpensive here because the
      // product limit is tiny (normally <=8 tracks and <=16 detector peaks).
      int[] assignments = SolveGlobalAssignment(tracks, clean);
      var usedPeaks = new HashSet<int>();

      for (int i = 0; i < tracks.Count; i++)
      {
        int peakIndex = assignments[i];
        if (peakIndex < 0) continue;

        State track = tracks[i];
        CwSignalCandidate peak = clean[peakIndex];
        UpdateMeasurement(track, peak);
        track.LastSeenUtc = utc;
        track.Active = true;
        track.SeenCount++;
        usedPeaks.Add(peakIndex);
      }

      // Birth new tracks only from unclaimed, spectrally distinct detections.
      for (int j = 0; j < clean.Count && tracks.Count < MaxTracks; j++)
      {
        if (usedPeaks.Contains(j)) continue;
        CwSignalCandidate peak = clean[j];
        if (tracks.Any(t =>
          Math.Abs(t.FrequencyHz - peak.FrequencyHz) < MinimumSeparationHz))
          continue;

        tracks.Add(new State
        {
          Id = nextId++,
          FrequencyHz = peak.FrequencyHz,
          DriftHzPerSecond = 0,
          P00 = 64,
          P01 = 0,
          P11 = 256,
          SnrDb = peak.SnrDb,
          FirstSeenUtc = utc,
          LastSeenUtc = utc,
          StateUtc = utc,
          SeenCount = 1,
          Active = true
        });
      }

      MarkMergedOrAmbiguousTracks();

      return tracks
        .OrderBy(t => t.FrequencyHz)
        .Select(t => new CwSignalTrack(
          t.Id,
          t.FrequencyHz,
          t.SnrDb,
          t.DriftHzPerSecond,
          t.FirstSeenUtc,
          t.LastSeenUtc,
          t.SeenCount >= 2 && utc - t.FirstSeenUtc >= ConfirmationDelay,
          t.Active,
          t.Ambiguous,
          Math.Sqrt(Math.Max(t.P00, 0))))
        .ToArray();
    }

    private List<CwSignalCandidate> SuppressDuplicateCandidates(
      IEnumerable<CwSignalCandidate> candidates)
    {
      var clean = new List<CwSignalCandidate>();
      foreach (CwSignalCandidate candidate in candidates
        .Where(c => double.IsFinite(c.FrequencyHz) &&
                    double.IsFinite(c.SnrDb) &&
                    c.FrequencyHz > 0)
        .OrderByDescending(c => c.SnrDb)
        .ThenBy(c => c.FrequencyHz))
      {
        if (clean.All(c =>
          Math.Abs(c.FrequencyHz - candidate.FrequencyHz) >=
          MinimumSeparationHz))
          clean.Add(candidate);

        // Bound association complexity independently of detector settings.
        if (clean.Count >= 16) break;
      }

      return clean.OrderBy(c => c.FrequencyHz).ToList();
    }

    private void Predict(State track, DateTime utc)
    {
      double dt = Math.Max(0, (utc - track.StateUtc).TotalSeconds);
      if (dt <= 0)
      {
        track.StateUtc = utc;
        return;
      }

      dt = Math.Min(dt, 2.0);
      track.FrequencyHz += track.DriftHzPerSecond * dt;

      // Constant velocity covariance prediction with white acceleration noise.
      double p00 = track.P00;
      double p01 = track.P01;
      double p11 = track.P11;
      double q = ProcessAccelerationSigma * ProcessAccelerationSigma;
      double dt2 = dt * dt;
      double dt3 = dt2 * dt;
      double dt4 = dt2 * dt2;

      track.P00 = p00 + 2 * dt * p01 + dt2 * p11 + q * dt4 / 4;
      track.P01 = p01 + dt * p11 + q * dt3 / 2;
      track.P11 = p11 + q * dt2;
      track.StateUtc = utc;
    }

    private double MeasurementVariance(CwSignalCandidate candidate)
    {
      // High SNR approaches the FFT/interpolation floor; weak peaks receive a
      // wider likelihood so they are not discarded by an unrealistically
      // sharp gate. Clamp to keep pathological dB values harmless.
      double snrLinear = Math.Pow(
        10, Math.Clamp(candidate.SnrDb, -20, 60) / 10.0);
      double sigma = BaseMeasurementSigmaHz +
        18.0 / Math.Sqrt(Math.Max(snrLinear, 0.01));
      sigma = Math.Clamp(sigma, BaseMeasurementSigmaHz, MatchToleranceHz / 2);
      return sigma * sigma;
    }

    private double AssociationCost(State track, CwSignalCandidate candidate)
    {
      double residual = candidate.FrequencyHz - track.FrequencyHz;
      if (Math.Abs(residual) > MatchToleranceHz)
        return double.PositiveInfinity;

      double innovationVariance = track.P00 + MeasurementVariance(candidate);
      double nis = residual * residual / Math.Max(innovationVariance, 1e-9);

      // 4-sigma statistical gate in addition to the hard Hz gate.
      if (nis > 16) return double.PositiveInfinity;

      // Frequency continuity dominates. SNR continuity is only a weak feature:
      // fading should never cause IDs to swap solely because strengths cross.
      double snrPenalty =
        Math.Min(Math.Abs(candidate.SnrDb - track.SnrDb), 30) / 30.0;
      return nis + 0.12 * snrPenalty;
    }

    private int[] SolveGlobalAssignment(
      IReadOnlyList<State> liveTracks,
      IReadOnlyList<CwSignalCandidate> peaks)
    {
      int trackCount = liveTracks.Count;
      var result = Enumerable.Repeat(-1, trackCount).ToArray();
      if (trackCount == 0 || peaks.Count == 0) return result;

      var memo = new Dictionary<(int Track, int Mask), (double Cost, int Choice)>();
      const double MissCost = 9.0;

      double Solve(int trackIndex, int usedMask)
      {
        if (trackIndex >= trackCount) return 0;
        var key = (trackIndex, usedMask);
        if (memo.TryGetValue(key, out var cached))
          return cached.Cost;

        double best = MissCost + Solve(trackIndex + 1, usedMask);
        int bestChoice = -1;

        for (int j = 0; j < peaks.Count; j++)
        {
          int bit = 1 << j;
          if ((usedMask & bit) != 0) continue;

          double association = AssociationCost(
            liveTracks[trackIndex], peaks[j]);
          if (!double.IsFinite(association)) continue;

          double candidateCost =
            association + Solve(trackIndex + 1, usedMask | bit);
          if (candidateCost < best)
          {
            best = candidateCost;
            bestChoice = j;
          }
        }

        memo[key] = (best, bestChoice);
        return best;
      }

      Solve(0, 0);
      int mask = 0;
      for (int i = 0; i < trackCount; i++)
      {
        if (!memo.TryGetValue((i, mask), out var node)) break;
        result[i] = node.Choice;
        if (node.Choice >= 0) mask |= 1 << node.Choice;
      }

      return result;
    }

    private void UpdateMeasurement(State track, CwSignalCandidate candidate)
    {
      double r = MeasurementVariance(candidate);
      double innovation = candidate.FrequencyHz - track.FrequencyHz;
      double s = track.P00 + r;
      double k0 = track.P00 / s;
      double k1 = track.P01 / s;

      track.FrequencyHz += k0 * innovation;
      track.DriftHzPerSecond += k1 * innovation;

      double oldP00 = track.P00;
      double oldP01 = track.P01;
      track.P00 = Math.Max(1e-6, (1 - k0) * oldP00);
      track.P01 = (1 - k0) * oldP01;
      track.P11 = Math.Max(1e-6, track.P11 - k1 * oldP01);

      track.DriftHzPerSecond =
        Math.Clamp(track.DriftHzPerSecond, -80, 80);
      track.SnrDb = 0.35 * track.SnrDb + 0.65 * candidate.SnrDb;
    }

    private void MarkMergedOrAmbiguousTracks()
    {
      for (int i = 0; i < tracks.Count; i++)
      {
        for (int j = i + 1; j < tracks.Count; j++)
        {
          if (Math.Abs(tracks[i].FrequencyHz - tracks[j].FrequencyHz) >
              MergeResolutionHz)
            continue;

          tracks[i].Ambiguous = true;
          tracks[j].Ambiguous = true;
        }
      }
    }
  }
}
