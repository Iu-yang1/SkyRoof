namespace SkyRoof.CW
{
  public sealed class CwFixedLagAssociatorOptions
  {
    public int SampleRate { get; init; } = SdrConst.AUDIO_SAMPLING_RATE;

    /// <summary>
    /// Number of future precision batches retained before the oldest batch is
    /// committed. With the default 120 ms precision hop, 3 batches = 360 ms.
    /// </summary>
    public int LagBatches { get; init; } = 3;

    public int MaxHypotheses { get; init; } = 24;
    public int AssignmentBeamWidth { get; init; } = 48;
    public int MaxPaths { get; init; } = 12;
    public int MaxMisses { get; init; } = 3;

    public double MaxLinkHz { get; init; } = 55;
    public double MaxSlopeHzPerSecond { get; init; } = 180;
    public double VelocityChangeSigmaHzPerSecond { get; init; } = 45;
    public double ProcessFrequencySigmaHz { get; init; } = 3.0;

    public double MissCost { get; init; } = 3.0;
    public double BirthCost { get; init; } = 2.4;

    /// <summary>
    /// A new, unanchored path must be observed in at least this many precision
    /// batches inside the lag window before its oldest observation is emitted.
    /// </summary>
    public int MinimumNewPathObservations { get; init; } = 2;
  }

  public readonly record struct CwAssociatedCandidateBatch(
    long CenterSampleIndex,
    IReadOnlyList<CwSignalCandidate> Candidates);

  /// <summary>
  /// Bounded fixed-lag multi-hypothesis association for precision CW ridges.
  ///
  /// This layer is intentionally separate from the Kalman/GNN track manager.
  /// It keeps several globally-consistent short-path hypotheses across a small
  /// future window, then commits only the oldest precision batch. Smooth
  /// constant-velocity paths beat identity swaps at crossings because future
  /// observations are available before an AssociationHintId is finalized.
  ///
  /// The algorithm is a small-target-count beam MHT, not a GLMB filter. It is
  /// bounded by MaxHypotheses, MaxPaths and LagBatches so runtime remains
  /// deterministic for the 5–8 lane desktop use case.
  /// </summary>
  public sealed class CwFixedLagAssociator
  {
    private sealed class Anchor
    {
      public int HintId;
      public long SampleIndex;
      public double FrequencyHz;
      public double VelocityHzPerSecond;
      public double SigmaHz;
      public int Misses;
    }

    private readonly record struct PathPoint(
      long SampleIndex,
      double FrequencyHz,
      double SigmaHz,
      double SnrDb,
      double ActivityProbability,
      int RidgePortionId,
      int ObservationIndex);

    private sealed class PathState
    {
      public long Key;
      public int HintId;

      public bool HasSeed;
      public long SeedSampleIndex;
      public double SeedFrequencyHz;
      public double SeedVelocityHzPerSecond;
      public double SeedSigmaHz;
      public int SeedMisses;

      public long LastSampleIndex;
      public double LastFrequencyHz;
      public double LastSigmaHz;
      public double LastSnrDb;
      public int LastRidgePortionId;
      public double VelocityHzPerSecond;
      public bool HasVelocity;
      public int Misses;

      public List<PathPoint> Points { get; } = [];

      public PathState Clone()
      {
        var result = new PathState
        {
          Key = Key,
          HintId = HintId,
          HasSeed = HasSeed,
          SeedSampleIndex = SeedSampleIndex,
          SeedFrequencyHz = SeedFrequencyHz,
          SeedVelocityHzPerSecond = SeedVelocityHzPerSecond,
          SeedSigmaHz = SeedSigmaHz,
          SeedMisses = SeedMisses,
          LastSampleIndex = LastSampleIndex,
          LastFrequencyHz = LastFrequencyHz,
          LastSigmaHz = LastSigmaHz,
          LastSnrDb = LastSnrDb,
          LastRidgePortionId = LastRidgePortionId,
          VelocityHzPerSecond = VelocityHzPerSecond,
          HasVelocity = HasVelocity,
          Misses = Misses
        };
        result.Points.AddRange(Points);
        return result;
      }
    }

    private sealed class Hypothesis
    {
      public double Cost;
      public List<PathState> Paths { get; } = [];

      public Hypothesis Clone()
      {
        var result = new Hypothesis
        {
          Cost = Cost
        };
        result.Paths.AddRange(
          Paths.Select(x => x.Clone()));
        return result;
      }
    }

    private sealed class PartialAssignment
    {
      public double Cost;
      public int UsedMask;
      public int[] Choices = [];

      public PartialAssignment Clone() =>
        new()
        {
          Cost = Cost,
          UsedMask = UsedMask,
          Choices = (int[])Choices.Clone()
        };
    }

    private readonly CwFixedLagAssociatorOptions options;
    private readonly List<CwRidgeObservationBatch> buffer = [];
    private readonly List<Anchor> anchors = [];
    private long lastInputSampleIndex = long.MinValue;
    private int nextHintId = 1;

    public CwFixedLagAssociator(
      CwFixedLagAssociatorOptions? options = null)
    {
      this.options =
        options ?? new CwFixedLagAssociatorOptions();
      ValidateOptions(this.options);
    }

    public CwFixedLagAssociatorOptions Options => options;
    public int PendingBatchCount => buffer.Count;

    public IReadOnlyList<CwAssociatedCandidateBatch> Push(
      CwRidgeObservationBatch batch)
    {
      if (batch.CenterSampleIndex <= lastInputSampleIndex)
        throw new ArgumentException(
          "Fixed-lag precision batches must have strictly increasing sample indices.",
          nameof(batch));

      lastInputSampleIndex = batch.CenterSampleIndex;
      buffer.Add(batch);

      var output = new List<CwAssociatedCandidateBatch>();
      while (buffer.Count > options.LagBatches)
      {
        Hypothesis best = SolveBuffer();
        output.Add(CommitOldest(best));
        buffer.RemoveAt(0);
      }

      return output;
    }

    /// <summary>
    /// Commits remaining buffered frames using whatever future context is
    /// available. Intended for stream shutdown/tests, not the steady-state
    /// receive path.
    /// </summary>
    public IReadOnlyList<CwAssociatedCandidateBatch> Flush()
    {
      var output = new List<CwAssociatedCandidateBatch>();
      while (buffer.Count > 0)
      {
        Hypothesis best = SolveBuffer();
        output.Add(CommitOldest(best));
        buffer.RemoveAt(0);
      }
      return output;
    }

    public void Reset()
    {
      buffer.Clear();
      anchors.Clear();
      lastInputSampleIndex = long.MinValue;
      nextHintId = 1;
    }

    private Hypothesis SolveBuffer()
    {
      var initial = new Hypothesis();
      foreach (Anchor anchor in anchors)
      {
        initial.Paths.Add(new PathState
        {
          Key = -anchor.HintId,
          HintId = anchor.HintId,
          HasSeed = true,
          SeedSampleIndex = anchor.SampleIndex,
          SeedFrequencyHz = anchor.FrequencyHz,
          SeedVelocityHzPerSecond =
            anchor.VelocityHzPerSecond,
          SeedSigmaHz = anchor.SigmaHz,
          SeedMisses = anchor.Misses,
          LastSampleIndex = anchor.SampleIndex,
          LastFrequencyHz = anchor.FrequencyHz,
          LastSigmaHz = anchor.SigmaHz,
          VelocityHzPerSecond =
            anchor.VelocityHzPerSecond,
          HasVelocity = true,
          Misses = anchor.Misses
        });
      }

      List<Hypothesis> hypotheses = [initial];
      foreach (CwRidgeObservationBatch batch in buffer)
      {
        var expanded = new List<Hypothesis>();
        foreach (Hypothesis hypothesis in hypotheses)
          expanded.AddRange(
            ExpandFrame(hypothesis, batch));

        hypotheses = expanded
          .OrderBy(x => x.Cost)
          .Take(options.MaxHypotheses)
          .ToList();

        if (hypotheses.Count == 0)
          hypotheses.Add(new Hypothesis());
      }

      return hypotheses
        .OrderBy(x => x.Cost)
        .First();
    }

    private IReadOnlyList<Hypothesis> ExpandFrame(
      Hypothesis hypothesis,
      CwRidgeObservationBatch batch)
    {
      CwRidgeObservation[] observations =
        batch.Observations
          .Where(x => x.KalmanEligible)
          .OrderBy(x => x.FrequencyHz)
          .Take(30)
          .ToArray();

      if (observations.Length >= 31)
        throw new InvalidOperationException(
          "Fixed-lag associator supports at most 30 precision observations per batch.");

      int existingCount = hypothesis.Paths.Count;
      var partials = new List<PartialAssignment>
      {
        new()
        {
          Cost = 0,
          UsedMask = 0,
          Choices = Enumerable.Repeat(
            -1, existingCount).ToArray()
        }
      };

      for (int pathIndex = 0;
           pathIndex < existingCount;
           pathIndex++)
      {
        PathState path = hypothesis.Paths[pathIndex];
        var next = new List<PartialAssignment>();

        foreach (PartialAssignment partial in partials)
        {
          var miss = partial.Clone();
          miss.Cost += options.MissCost *
            (1 + 0.35 * path.Misses);
          miss.Choices[pathIndex] = -1;
          next.Add(miss);

          for (int observationIndex = 0;
               observationIndex < observations.Length;
               observationIndex++)
          {
            int bit = 1 << observationIndex;
            if ((partial.UsedMask & bit) != 0)
              continue;

            double linkCost = LinkCost(
              path,
              observations[observationIndex],
              batch.CenterSampleIndex);
            if (!double.IsFinite(linkCost))
              continue;

            var linked = partial.Clone();
            linked.Cost += linkCost;
            linked.UsedMask |= bit;
            linked.Choices[pathIndex] =
              observationIndex;
            next.Add(linked);
          }
        }

        partials = next
          .OrderBy(x => x.Cost)
          .Take(options.AssignmentBeamWidth)
          .ToList();
      }

      var results = new List<Hypothesis>();
      foreach (PartialAssignment assignment in partials)
      {
        Hypothesis expanded = hypothesis.Clone();
        expanded.Cost += assignment.Cost;

        for (int i = 0;
             i < existingCount;
             i++)
        {
          PathState path = expanded.Paths[i];
          int observationIndex =
            assignment.Choices[i];

          if (observationIndex < 0)
          {
            path.Misses++;
            continue;
          }

          AppendObservation(
            path,
            observations[observationIndex],
            observationIndex);
        }

        for (int observationIndex = 0;
             observationIndex < observations.Length;
             observationIndex++)
        {
          int bit = 1 << observationIndex;
          if ((assignment.UsedMask & bit) != 0)
            continue;

          CwRidgeObservation observation =
            observations[observationIndex];
          expanded.Cost += options.BirthCost +
            0.5 * (1 -
              Math.Clamp(
                observation.ActivityProbability,
                0, 1));

          var birth = new PathState
          {
            Key = MakeBirthKey(
              batch.CenterSampleIndex,
              observationIndex),
            LastSampleIndex =
              observation.CenterSampleIndex,
            LastFrequencyHz =
              observation.FrequencyHz,
            LastSigmaHz =
              Math.Max(
                observation.MeasurementSigmaHz,
                0.5),
            LastSnrDb = observation.SnrDb,
            LastRidgePortionId =
              observation.RidgePortionId,
            Misses = 0
          };
          birth.Points.Add(new(
            observation.CenterSampleIndex,
            observation.FrequencyHz,
            Math.Max(
              observation.MeasurementSigmaHz,
              0.5),
            observation.SnrDb,
            observation.ActivityProbability,
            observation.RidgePortionId,
            observationIndex));
          expanded.Paths.Add(birth);
        }

        expanded.Paths.RemoveAll(
          x => x.Misses > options.MaxMisses);

        if (expanded.Paths.Count > options.MaxPaths)
        {
          // Never throw away an anchored identity before a tentative path.
          PathState[] keep = expanded.Paths
            .OrderByDescending(x => x.HintId != 0)
            .ThenBy(x => x.Misses)
            .ThenByDescending(x =>
              x.Points.Count)
            .ThenByDescending(x => x.LastSnrDb)
            .Take(options.MaxPaths)
            .ToArray();
          expanded.Paths.Clear();
          expanded.Paths.AddRange(keep);
        }

        results.Add(expanded);
      }

      return results
        .OrderBy(x => x.Cost)
        .Take(options.MaxHypotheses)
        .ToArray();
    }

    private double LinkCost(
      PathState path,
      CwRidgeObservation observation,
      long batchSampleIndex)
    {
      long targetSample =
        observation.CenterSampleIndex != 0
          ? observation.CenterSampleIndex
          : batchSampleIndex;
      double dt =
        (targetSample - path.LastSampleIndex) /
        (double)options.SampleRate;
      if (dt <= 0)
        return double.PositiveInfinity;

      double predicted =
        path.LastFrequencyHz +
        (path.HasVelocity
          ? path.VelocityHzPerSecond * dt
          : 0);

      double sigma = Math.Sqrt(
        path.LastSigmaHz * path.LastSigmaHz +
        observation.MeasurementSigmaHz *
          observation.MeasurementSigmaHz +
        options.ProcessFrequencySigmaHz *
          options.ProcessFrequencySigmaHz *
          Math.Max(1.0, dt / 0.12));

      double gate = Math.Max(
        options.MaxLinkHz,
        options.MaxSlopeHzPerSecond * dt +
          4.0 * sigma);
      double residual =
        observation.FrequencyHz - predicted;
      if (Math.Abs(residual) > gate)
        return double.PositiveInfinity;

      double newVelocity =
        (observation.FrequencyHz -
         path.LastFrequencyHz) / dt;
      if (Math.Abs(newVelocity) >
          options.MaxSlopeHzPerSecond * 1.5)
        return double.PositiveInfinity;

      double cost =
        residual * residual /
        Math.Max(sigma * sigma, 1e-9);

      if (path.HasVelocity)
      {
        double velocityDelta =
          newVelocity -
          path.VelocityHzPerSecond;
        double normalized =
          velocityDelta /
          options.VelocityChangeSigmaHzPerSecond;
        cost += 0.45 * normalized * normalized;
      }

      double snrDelta =
        Math.Min(
          Math.Abs(
            observation.SnrDb -
            path.LastSnrDb),
          30);
      cost += 0.04 * snrDelta;

      if (path.LastRidgePortionId != 0 &&
          observation.RidgePortionId != 0 &&
          path.LastRidgePortionId ==
            observation.RidgePortionId)
        cost -= 0.25;

      cost += 0.20 *
        (1 - Math.Clamp(
          observation.ActivityProbability,
          0, 1));

      return cost;
    }

    private void AppendObservation(
      PathState path,
      CwRidgeObservation observation,
      int observationIndex)
    {
      double dt =
        (observation.CenterSampleIndex -
         path.LastSampleIndex) /
        (double)options.SampleRate;

      if (dt > 0)
      {
        double velocity =
          (observation.FrequencyHz -
           path.LastFrequencyHz) / dt;

        path.VelocityHzPerSecond =
          path.HasVelocity
            ? 0.25 * path.VelocityHzPerSecond +
              0.75 * velocity
            : velocity;
        path.VelocityHzPerSecond =
          Math.Clamp(
            path.VelocityHzPerSecond,
            -options.MaxSlopeHzPerSecond,
            options.MaxSlopeHzPerSecond);
        path.HasVelocity = true;
      }

      path.LastSampleIndex =
        observation.CenterSampleIndex;
      path.LastFrequencyHz =
        observation.FrequencyHz;
      path.LastSigmaHz =
        Math.Max(
          observation.MeasurementSigmaHz,
          0.5);
      path.LastSnrDb =
        observation.SnrDb;
      path.LastRidgePortionId =
        observation.RidgePortionId;
      path.Misses = 0;
      path.Points.Add(new(
        observation.CenterSampleIndex,
        observation.FrequencyHz,
        Math.Max(
          observation.MeasurementSigmaHz,
          0.5),
        observation.SnrDb,
        observation.ActivityProbability,
        observation.RidgePortionId,
        observationIndex));
    }

    private CwAssociatedCandidateBatch CommitOldest(
      Hypothesis best)
    {
      CwRidgeObservationBatch oldest =
        buffer[0];
      CwRidgeObservation[] observations =
        oldest.Observations
          .Where(x => x.KalmanEligible)
          .OrderBy(x => x.FrequencyHz)
          .ToArray();

      var emitted =
        new List<CwSignalCandidate>();

      foreach (var item in observations
        .Select((observation, index) =>
          (Observation: observation, Index: index)))
      {
        PathState? path = best.Paths.FirstOrDefault(
          p => p.Points.Any(point =>
            point.SampleIndex ==
              oldest.CenterSampleIndex &&
            point.ObservationIndex ==
              item.Index));
        if (path == null)
          continue;

        bool existingIdentity =
          path.HintId != 0;
        int observationsInLag =
          path.Points.Count;
        if (!existingIdentity &&
            observationsInLag <
              options.MinimumNewPathObservations)
          continue;

        if (path.HintId == 0)
          path.HintId = nextHintId++;

        CwSignalCandidate candidate =
          item.Observation.ToCandidate();
        emitted.Add(candidate with
        {
          AssociationHintId =
            path.HintId
        });
      }

      UpdateAnchors(
        best,
        oldest.CenterSampleIndex);

      return new(
        oldest.CenterSampleIndex,
        emitted
          .OrderBy(x => x.FrequencyHz)
          .ToArray());
    }

    private void UpdateAnchors(
      Hypothesis best,
      long commitSampleIndex)
    {
      var updated = new List<Anchor>();

      foreach (PathState path in best.Paths
        .Where(x => x.HintId != 0))
      {
        bool observedAtCommit =
          path.Points.Any(x =>
            x.SampleIndex ==
            commitSampleIndex);

        (
          double frequency,
          double velocity,
          double sigma) =
            FitPathAt(
              path,
              commitSampleIndex);

        int misses = observedAtCommit
          ? 0
          : Math.Min(
              options.MaxMisses + 1,
              path.SeedMisses + 1);
        if (misses > options.MaxMisses)
          continue;

        updated.Add(new Anchor
        {
          HintId = path.HintId,
          SampleIndex = commitSampleIndex,
          FrequencyHz = frequency,
          VelocityHzPerSecond = velocity,
          SigmaHz = sigma,
          Misses = misses
        });
      }

      anchors.Clear();
      anchors.AddRange(
        updated
          .GroupBy(x => x.HintId)
          .Select(g => g.First())
          .OrderBy(x => x.HintId));
    }

    private (
      double Frequency,
      double Velocity,
      double Sigma)
      FitPathAt(
        PathState path,
        long targetSampleIndex)
    {
      var samples =
        new List<(double T, double F, double W)>();

      if (path.HasSeed)
      {
        double t =
          (path.SeedSampleIndex -
           targetSampleIndex) /
          (double)options.SampleRate;
        double sigma =
          Math.Max(path.SeedSigmaHz, 1.0);
        samples.Add(
          (t,
           path.SeedFrequencyHz,
           1.0 / (sigma * sigma)));
      }

      foreach (PathPoint point in path.Points)
      {
        double t =
          (point.SampleIndex -
           targetSampleIndex) /
          (double)options.SampleRate;
        double sigma =
          Math.Max(point.SigmaHz, 0.75);
        samples.Add(
          (t,
           point.FrequencyHz,
           1.0 / (sigma * sigma)));
      }

      if (samples.Count == 0)
      {
        double dt =
          (targetSampleIndex -
           path.LastSampleIndex) /
          (double)options.SampleRate;
        return (
          path.LastFrequencyHz +
            (path.HasVelocity
              ? path.VelocityHzPerSecond * dt
              : 0),
          path.HasVelocity
            ? path.VelocityHzPerSecond
            : 0,
          Math.Max(path.LastSigmaHz, 2));
      }

      double sw = samples.Sum(x => x.W);
      double st = samples.Sum(x => x.W * x.T);
      double sf = samples.Sum(x => x.W * x.F);
      double stt =
        samples.Sum(x => x.W * x.T * x.T);
      double stf =
        samples.Sum(x => x.W * x.T * x.F);

      double determinant =
        sw * stt - st * st;
      double intercept;
      double slope;

      if (samples.Count >= 2 &&
          Math.Abs(determinant) > 1e-12)
      {
        intercept =
          (sf * stt - st * stf) /
          determinant;
        slope =
          (sw * stf - st * sf) /
          determinant;
      }
      else
      {
        intercept = sf / Math.Max(sw, 1e-12);
        slope = path.HasVelocity
          ? path.VelocityHzPerSecond
          : 0;
      }

      slope = Math.Clamp(
        slope,
        -options.MaxSlopeHzPerSecond,
        options.MaxSlopeHzPerSecond);

      double sigmaOut = Math.Sqrt(
        1.0 / Math.Max(sw, 1e-12));
      sigmaOut = Math.Clamp(
        sigmaOut, 0.75, options.MaxLinkHz);

      return (
        intercept,
        slope,
        sigmaOut);
    }

    private static long MakeBirthKey(
      long sampleIndex,
      int observationIndex)
    {
      unchecked
      {
        return
          (sampleIndex * 1315423911L) ^
          (observationIndex + 1);
      }
    }

    private static void ValidateOptions(
      CwFixedLagAssociatorOptions value)
    {
      if (value.SampleRate < 1000 ||
          value.SampleRate > 384000)
        throw new ArgumentOutOfRangeException(
          nameof(value.SampleRate));
      if (value.LagBatches is < 1 or > 8)
        throw new ArgumentOutOfRangeException(
          nameof(value.LagBatches));
      if (value.MaxHypotheses is < 2 or > 256)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxHypotheses));
      if (value.AssignmentBeamWidth is < 4 or > 512)
        throw new ArgumentOutOfRangeException(
          nameof(value.AssignmentBeamWidth));
      if (value.MaxPaths is < 2 or > 30)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxPaths));
      if (value.MaxMisses is < 0 or > 12)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxMisses));
      if (!double.IsFinite(value.MaxLinkHz) ||
          value.MaxLinkHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxLinkHz));
      if (!double.IsFinite(
            value.MaxSlopeHzPerSecond) ||
          value.MaxSlopeHzPerSecond <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.MaxSlopeHzPerSecond));
      if (!double.IsFinite(
            value.VelocityChangeSigmaHzPerSecond) ||
          value.VelocityChangeSigmaHzPerSecond <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.VelocityChangeSigmaHzPerSecond));
      if (!double.IsFinite(
            value.ProcessFrequencySigmaHz) ||
          value.ProcessFrequencySigmaHz <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.ProcessFrequencySigmaHz));
      if (!double.IsFinite(value.MissCost) ||
          value.MissCost <= 0 ||
          !double.IsFinite(value.BirthCost) ||
          value.BirthCost <= 0)
        throw new ArgumentOutOfRangeException(
          nameof(value.MissCost));
      if (value.MinimumNewPathObservations is < 1 or > 8)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumNewPathObservations));
    }
  }
}
