using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  /// <summary>A peak measured by a CW detector, in audio-frequency Hz.</summary>
  public readonly record struct CwSignalCandidate(
    double FrequencyHz,
    double SnrDb,
    double MeasurementSigmaHz = 0,
    double ResolutionHz = 0,
    double ActivityProbability = 1);

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
    double FrequencySigmaHz = 0,
    int MergeGroupId = 0,
    double IdentityConfidence = 1);

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
      public int MergeGroupId;
      public double MergeEntryFrequencyHz;
      public double MergeEntryDriftHzPerSecond;
      public DateTime MergeEntryUtc;
      public DateTime IdentityAnchorUntilUtc;
    }

    private readonly List<State> tracks = new();
    private DateTime? previousUpdateUtc;
    private int nextId = 1;
    private int nextMergeGroupId = 1;

    public int MaxTracks { get; }
    /// <summary>
    /// Minimum separation used only when deciding whether an unexplained
    /// candidate may create a new labelled track. It is not a detector peak
    /// de-duplication threshold and not a statement of STFT resolution.
    /// </summary>
    public double TrackBirthGateHz { get; }

    [Obsolete("Use TrackBirthGateHz; detector de-duplication is separate.")]
    public double MinimumSeparationHz => TrackBirthGateHz;

    public double CandidateDeduplicationHz { get; }
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

    /// <summary>
    /// Short labelled-history horizon used when a finite-resolution peak may
    /// represent more than one ridge. This is an identity prior, not a claim
    /// of a full Bayesian fixed-lag smoother.
    /// </summary>
    public TimeSpan FixedLagIdentityTime { get; }

    public CwPileupTrackManager(
      int maxTracks = 8,
      double minimumSeparationHz = 8,
      double matchToleranceHz = 40,
      TimeSpan? confirmationDelay = null,
      TimeSpan? holdTime = null,
      double processAccelerationSigma = 8,
      double baseMeasurementSigmaHz = 3,
      double mergeResolutionHz = 18,
      double candidateDeduplicationHz = 2,
      TimeSpan? fixedLagIdentityTime = null)
    {
      if (maxTracks is < 1 or > 32)
        throw new ArgumentOutOfRangeException(nameof(maxTracks));
      if (!double.IsFinite(minimumSeparationHz) || minimumSeparationHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(minimumSeparationHz));
      if (!double.IsFinite(matchToleranceHz) || matchToleranceHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(matchToleranceHz));
      if (!double.IsFinite(candidateDeduplicationHz) ||
          candidateDeduplicationHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(candidateDeduplicationHz));
      if (!double.IsFinite(processAccelerationSigma) || processAccelerationSigma <= 0)
        throw new ArgumentOutOfRangeException(nameof(processAccelerationSigma));
      if (!double.IsFinite(baseMeasurementSigmaHz) || baseMeasurementSigmaHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(baseMeasurementSigmaHz));
      if (!double.IsFinite(mergeResolutionHz) || mergeResolutionHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(mergeResolutionHz));

      MaxTracks = maxTracks;
      TrackBirthGateHz = minimumSeparationHz;
      CandidateDeduplicationHz = candidateDeduplicationHz;
      MatchToleranceHz = matchToleranceHz;
      ConfirmationDelay = confirmationDelay ?? TimeSpan.FromMilliseconds(400);
      HoldTime = holdTime ?? TimeSpan.FromSeconds(5);
      ProcessAccelerationSigma = processAccelerationSigma;
      BaseMeasurementSigmaHz = baseMeasurementSigmaHz;
      MergeResolutionHz = mergeResolutionHz;
      FixedLagIdentityTime =
        fixedLagIdentityTime ?? TimeSpan.FromMilliseconds(350);

      if (FixedLagIdentityTime <= TimeSpan.Zero ||
          FixedLagIdentityTime > TimeSpan.FromSeconds(2))
        throw new ArgumentOutOfRangeException(
          nameof(fixedLagIdentityTime));
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
      nextMergeGroupId = 1;
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

      var clean = PrepareCandidates(candidates);

      // Predict every live ridge to this scan before doing any association.
      foreach (State track in tracks)
      {
        Predict(track, utc);
        track.Active = false;
        track.Ambiguous = false;
      }

      UpdateMergeGroupsBeforeAssociation(utc);

      // Exact global min-cost association is inexpensive here because the
      // product limit is tiny (normally <=8 tracks and <=16 detector peaks).
      int[] assignments = SolveGlobalAssignment(
        tracks, clean, utc);
      var usedPeaks = new HashSet<int>();
      var handledTracks = new HashSet<int>();

      // A close pair can produce one finite-resolution peak. For a short,
      // bounded lag, treat that peak as evidence for the merge group instead
      // of forcing it onto exactly one identity. Apply only a common centroid
      // correction so the pre-merge relative frequency/velocity survives.
      foreach (IGrouping<int, (State Track, int Index)> group in tracks
        .Select((track, index) => (Track: track, Index: index))
        .Where(x => x.Track.MergeGroupId != 0)
        .GroupBy(x => x.Track.MergeGroupId))
      {
        var members = group.ToArray();
        int[] assignedPeaks = members
          .Select(x => assignments[x.Index])
          .Where(x => x >= 0)
          .Distinct()
          .ToArray();

        DateTime mergeStart = members
          .Min(x => x.Track.MergeEntryUtc);
        bool withinLag =
          utc - mergeStart <= FixedLagIdentityTime;
        if (!withinLag ||
            assignedPeaks.Length != 1 ||
            members.Length < 2)
          continue;

        int peakIndex = assignedPeaks[0];
        CwSignalCandidate mergedPeak = clean[peakIndex];
        double predictedCentroid =
          members.Average(x => x.Track.FrequencyHz);
        double residual =
          mergedPeak.FrequencyHz - predictedCentroid;
        double commonCorrection = Math.Clamp(
          0.25 * residual,
          -MergeResolutionHz / 2.0,
          MergeResolutionHz / 2.0);

        foreach (var member in members)
        {
          State track = member.Track;
          track.FrequencyHz += commonCorrection;
          track.P00 = Math.Max(
            track.P00,
            MeasurementVariance(mergedPeak));
          track.SnrDb =
            0.7 * track.SnrDb +
            0.3 * mergedPeak.SnrDb;
          track.LastSeenUtc = utc;
          track.Active = true;
          track.Ambiguous = true;
          if (track.SeenCount > 1)
            track.SeenCount++;
          handledTracks.Add(member.Index);
        }

        usedPeaks.Add(peakIndex);
      }

      for (int i = 0; i < tracks.Count; i++)
      {
        if (handledTracks.Contains(i)) continue;
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
      // Capacity is spent on the strongest unexplained peaks, not on the
      // lowest-frequency peaks merely because the detector list is sorted.
      foreach (int j in Enumerable.Range(0, clean.Count)
        .Where(index => !usedPeaks.Contains(index))
        .OrderByDescending(index => clean[index].SnrDb)
        .ThenBy(index => clean[index].FrequencyHz))
      {
        if (tracks.Count >= MaxTracks) break;

        CwSignalCandidate peak = clean[j];
        double birthGate = Math.Max(
          TrackBirthGateHz,
          peak.ResolutionHz > 0 ? peak.ResolutionHz * 0.5 : 0);
        if (tracks.Any(t =>
          Math.Abs(t.FrequencyHz - peak.FrequencyHz) < birthGate))
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
          Math.Sqrt(Math.Max(t.P00, 0)),
          t.MergeGroupId,
          IdentityConfidence(t, utc)))
        .ToArray();
    }

    private List<CwSignalCandidate> PrepareCandidates(
      IEnumerable<CwSignalCandidate> candidates)
    {
      // The detector owns spectral peak de-duplication. The tracker only
      // removes effectively identical numerical duplicates so that a precise
      // scanner may legitimately present two resolvable ridges 10–20 Hz apart.
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
          CandidateDeduplicationHz))
          clean.Add(candidate);

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
      if (double.IsFinite(candidate.MeasurementSigmaHz) &&
          candidate.MeasurementSigmaHz > 0)
      {
        double supplied = Math.Clamp(
          candidate.MeasurementSigmaHz,
          BaseMeasurementSigmaHz * 0.25,
          MatchToleranceHz);
        return supplied * supplied;
      }

      double snrLinear = Math.Pow(
        10, Math.Clamp(candidate.SnrDb, -20, 60) / 10.0);
      double sigma = BaseMeasurementSigmaHz +
        18.0 / Math.Sqrt(Math.Max(snrLinear, 0.01));
      sigma = Math.Clamp(sigma, BaseMeasurementSigmaHz, MatchToleranceHz / 2);
      return sigma * sigma;
    }

    private double AssociationCost(
      State track,
      CwSignalCandidate candidate,
      DateTime utc)
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

      double identityPenalty = 0;
      bool hasIdentityAnchor =
        track.MergeEntryUtc != default &&
        (track.MergeGroupId != 0 ||
         utc <= track.IdentityAnchorUntilUtc);
      if (hasIdentityAnchor)
      {
        double dt =
          (utc - track.MergeEntryUtc).TotalSeconds;
        double anchorPrediction =
          track.MergeEntryFrequencyHz +
          track.MergeEntryDriftHzPerSecond * dt;
        double anchorSigma = Math.Max(
          MergeResolutionHz,
          Math.Sqrt(Math.Max(track.P00, 1)));
        double anchorResidual =
          candidate.FrequencyHz - anchorPrediction;
        identityPenalty =
          0.45 * anchorResidual * anchorResidual /
          (anchorSigma * anchorSigma);
      }

      return nis + 0.12 * snrPenalty + identityPenalty;
    }

    private int[] SolveGlobalAssignment(
      IReadOnlyList<State> liveTracks,
      IReadOnlyList<CwSignalCandidate> peaks,
      DateTime utc)
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
            liveTracks[trackIndex], peaks[j], utc);
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

    private void UpdateMergeGroupsBeforeAssociation(DateTime utc)
    {
      if (tracks.Count < 2) return;

      var adjacency = new bool[tracks.Count, tracks.Count];
      for (int i = 0; i < tracks.Count; i++)
      {
        for (int j = i + 1; j < tracks.Count; j++)
        {
          double separation =
            Math.Abs(tracks[i].FrequencyHz -
                     tracks[j].FrequencyHz);
          double sigmaOverlap = Math.Sqrt(Math.Max(
            0, tracks[i].P00 + tracks[j].P00));
          double threshold =
            MergeResolutionHz +
            Math.Min(MergeResolutionHz, sigmaOverlap);
          if (separation <= threshold)
          {
            adjacency[i, j] = true;
            adjacency[j, i] = true;
          }
        }
      }

      var grouped = new bool[tracks.Count];
      var inAnyGroup = new bool[tracks.Count];

      for (int start = 0; start < tracks.Count; start++)
      {
        if (grouped[start]) continue;
        var component = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        grouped[start] = true;

        while (queue.Count > 0)
        {
          int i = queue.Dequeue();
          component.Add(i);
          for (int j = 0; j < tracks.Count; j++)
          {
            if (!adjacency[i, j] || grouped[j]) continue;
            grouped[j] = true;
            queue.Enqueue(j);
          }
        }

        if (component.Count < 2) continue;

        int groupId = component
          .Select(i => tracks[i].MergeGroupId)
          .Where(id => id != 0)
          .DefaultIfEmpty(0)
          .Min();
        if (groupId == 0)
          groupId = nextMergeGroupId++;

        foreach (int index in component)
        {
          State track = tracks[index];
          inAnyGroup[index] = true;
          track.Ambiguous = true;

          if (track.MergeGroupId == 0)
          {
            track.MergeEntryFrequencyHz =
              track.FrequencyHz;
            track.MergeEntryDriftHzPerSecond =
              track.DriftHzPerSecond;
            track.MergeEntryUtc = utc;
          }

          track.MergeGroupId = groupId;
          track.IdentityAnchorUntilUtc =
            utc + FixedLagIdentityTime;
        }
      }

      for (int i = 0; i < tracks.Count; i++)
      {
        if (inAnyGroup[i]) continue;
        State track = tracks[i];
        if (track.MergeGroupId != 0)
        {
          track.MergeGroupId = 0;
          track.IdentityAnchorUntilUtc =
            utc + FixedLagIdentityTime;
        }
      }
    }

    private void MarkMergedOrAmbiguousTracks()
    {
      for (int i = 0; i < tracks.Count; i++)
      {
        if (tracks[i].MergeGroupId != 0)
          tracks[i].Ambiguous = true;

        for (int j = i + 1; j < tracks.Count; j++)
        {
          double separation =
            Math.Abs(tracks[i].FrequencyHz -
                     tracks[j].FrequencyHz);
          double uncertaintyOverlap = 2.0 *
            Math.Sqrt(Math.Max(
              0, tracks[i].P00 + tracks[j].P00));

          if (separation >
              MergeResolutionHz + uncertaintyOverlap)
            continue;

          tracks[i].Ambiguous = true;
          tracks[j].Ambiguous = true;
        }
      }
    }

    private double IdentityConfidence(
      State track,
      DateTime utc)
    {
      if (track.MergeGroupId != 0)
        return 0.45;
      if (track.MergeEntryUtc != default &&
          utc <= track.IdentityAnchorUntilUtc)
        return 0.75;
      if (!track.Active)
        return 0.85;
      return 1.0;
    }

  }
}
