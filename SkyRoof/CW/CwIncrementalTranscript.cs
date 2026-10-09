using System.Text;

namespace SkyRoof.CW
{
  public sealed class CwTranscriptOptions
  {
    public double SymbolMatchToleranceSeconds { get; init; } = 0.22;
    public double CommitLagSeconds { get; init; } = 0.90;
    public double AbandonAfterSeconds { get; init; } = 2.5;
    public int MinimumConfirmations { get; init; } = 2;
    public double MinimumConsensus { get; init; } = 0.60;
    public double MinimumAverageConfidence { get; init; } = 0.52;
    public double CommittedMatchRetentionSeconds { get; init; } = 8.0;
  }

  public readonly record struct CwTranscriptSymbol(
    char Character,
    DateTime TimestampUtc,
    double Confidence,
    int Confirmations,
    bool Committed);

  public sealed class CwTranscriptSnapshot
  {
    public int TrackId { get; }
    public int AssociationHintId { get; }
    public string CommittedText { get; }
    public string ProvisionalText { get; }
    public string Text => CommittedText + ProvisionalText;
    public IReadOnlyList<CwTranscriptSymbol> ProvisionalSymbols { get; }
    public DateTime LastWindowEndUtc { get; }

    public CwTranscriptSnapshot(
      int trackId,
      int associationHintId,
      string committedText,
      string provisionalText,
      IReadOnlyList<CwTranscriptSymbol> provisionalSymbols,
      DateTime lastWindowEndUtc)
    {
      TrackId = trackId;
      AssociationHintId = associationHintId;
      CommittedText = committedText;
      ProvisionalText = provisionalText;
      ProvisionalSymbols = provisionalSymbols;
      LastWindowEndUtc = lastWindowEndUtc;
    }
  }

  /// <summary>
  /// Reconciles overlapping DeepCW/CTC windows into one continuous transcript
  /// per labelled CW lane.
  ///
  /// DeepCW already exposes the CTC output frame for each emitted character.
  /// We map those frames to approximate absolute UTC positions inside the
  /// decode window, sequence-align new symbols against recent time clusters,
  /// and vote before committing a stable prefix. AssociationHintId is the
  /// preferred identity key, so text remains attached to the multi-frame ridge
  /// identity even if a tracker instance is rebuilt.
  /// </summary>
  public sealed class CwIncrementalTranscriptCoordinator
  {
    private readonly record struct LaneKey(
      bool UsesAssociationHint,
      int Id);

    private sealed class Vote
    {
      public double Weight;
      public int Confirmations;
      public double ConfidenceSum;
    }

    private sealed class SymbolCluster
    {
      public long Id;
      public DateTime TimestampUtc;
      public double TimeWeight;
      public readonly Dictionary<char, Vote> Votes = [];
      public readonly HashSet<long> WindowIds = [];
      public DateTime LastObservedWindowEndUtc;
      public bool Committed;
      public char CommittedCharacter;
    }

    private sealed class LaneState
    {
      public int TrackId;
      public int AssociationHintId;
      public DateTime LastWindowEndUtc;
      public readonly StringBuilder Committed = new();
      public readonly List<SymbolCluster> Provisional = [];
      public readonly List<SymbolCluster> RecentCommitted = [];
    }

    private readonly record struct IncomingSymbol(
      char Character,
      DateTime TimestampUtc,
      double Confidence);

    private enum AlignmentAction : byte
    {
      None,
      SkipExisting,
      InsertIncoming,
      Match
    }

    private readonly CwTranscriptOptions options;
    private readonly Dictionary<LaneKey, LaneState> lanes = [];
    private long nextClusterId = 1;

    public CwIncrementalTranscriptCoordinator(
      CwTranscriptOptions? options = null)
    {
      this.options = options ?? new CwTranscriptOptions();
      ValidateOptions(this.options);
    }

    public CwTranscriptOptions Options => options;

    public CwTranscriptSnapshot Push(
      DeepCwLaneResult result)
    {
      ValidateResult(result);

      LaneKey key = IdentityKey(result);
      if (!lanes.TryGetValue(key, out LaneState? state))
      {
        state = new LaneState
        {
          TrackId = result.TrackId,
          AssociationHintId =
            result.AssociationHintId,
          LastWindowEndUtc =
            result.WindowEndUtc
        };
        lanes.Add(key, state);
      }
      else
      {
        if (result.WindowEndUtc <
            state.LastWindowEndUtc)
          throw new ArgumentException(
            "CW transcript windows must be monotonic per lane.",
            nameof(result));

        state.TrackId = result.TrackId;
        if (result.AssociationHintId != 0)
          state.AssociationHintId =
            result.AssociationHintId;
        state.LastWindowEndUtc =
          result.WindowEndUtc;
      }

      IncomingSymbol[] incoming =
        MapSymbols(result);
      if (incoming.Length > 0)
        MergeWindow(
          state,
          result.WindowEndUtc.Ticks,
          result.WindowEndUtc,
          incoming);

      CommitStablePrefix(
        state,
        result.WindowEndUtc);
      PruneCommittedMatchHistory(
        state,
        result.WindowEndUtc);

      return Snapshot(state);
    }

    public IReadOnlyList<CwTranscriptSnapshot> Push(
      IEnumerable<DeepCwLaneResult> results)
    {
      ArgumentNullException.ThrowIfNull(results);
      var snapshots = new List<CwTranscriptSnapshot>();

      foreach (DeepCwLaneResult result in results
        .OrderBy(x => x.WindowEndUtc)
        .ThenBy(x =>
          x.AssociationHintId != 0
            ? x.AssociationHintId
            : x.TrackId))
      {
        snapshots.Add(Push(result));
      }

      return snapshots;
    }

    public CwTranscriptSnapshot? Get(
      int trackId,
      int associationHintId = 0)
    {
      LaneKey key = associationHintId != 0
        ? new(true, associationHintId)
        : new(false, trackId);

      return lanes.TryGetValue(
        key, out LaneState? state)
        ? Snapshot(state)
        : null;
    }

    public IReadOnlyList<CwTranscriptSnapshot> GetAll() =>
      lanes.Values
        .OrderBy(x =>
          x.AssociationHintId != 0
            ? x.AssociationHintId
            : int.MaxValue)
        .ThenBy(x => x.TrackId)
        .Select(Snapshot)
        .ToArray();

    public void Reset()
    {
      lanes.Clear();
      nextClusterId = 1;
    }

    private IncomingSymbol[] MapSymbols(
      DeepCwLaneResult result)
    {
      IReadOnlyList<DeepCwDecodedSymbol> symbols =
        result.Symbols ??
        Array.Empty<DeepCwDecodedSymbol>();
      if (symbols.Count == 0)
        return [];

      int frameCount =
        result.OutputFrameCount > 0
          ? result.OutputFrameCount
          : symbols.Max(x => x.OutputFrame) + 1;
      if (frameCount <= 0)
        return [];

      DateTime start =
        result.WindowEndUtc -
        TimeSpan.FromSeconds(
          result.WindowDurationSeconds);

      var mapped = new List<IncomingSymbol>(
        symbols.Count);

      foreach (DeepCwDecodedSymbol symbol in symbols)
      {
        if (symbol.OutputFrame < 0 ||
            symbol.OutputFrame >= frameCount)
          continue;

        // Treat the ONNX output index as the center of a temporal cell. The
        // model's exact receptive-field center is not published in metadata,
        // so this normalized mapping is intentionally approximate but stable
        // across overlapping windows.
        double fraction =
          (symbol.OutputFrame + 0.5) /
          frameCount;
        DateTime utc =
          start +
          TimeSpan.FromSeconds(
            fraction *
            result.WindowDurationSeconds);

        mapped.Add(new(
          symbol.Character,
          utc,
          Math.Clamp(
            symbol.Confidence,
            0, 1)));
      }

      return mapped
        .OrderBy(x => x.TimestampUtc)
        .ToArray();
    }

    private void MergeWindow(
      LaneState state,
      long windowId,
      DateTime windowEndUtc,
      IReadOnlyList<IncomingSymbol> incoming)
    {
      double tolerance =
        options.SymbolMatchToleranceSeconds;

      DateTime firstIncoming =
        incoming[0].TimestampUtc -
        TimeSpan.FromSeconds(tolerance);
      DateTime lastIncoming =
        incoming[^1].TimestampUtc +
        TimeSpan.FromSeconds(tolerance);

      SymbolCluster[] existing =
        state.RecentCommitted
          .Concat(state.Provisional)
          .Where(x =>
            x.TimestampUtc >= firstIncoming &&
            x.TimestampUtc <= lastIncoming)
          .OrderBy(x => x.TimestampUtc)
          .ThenBy(x => x.Id)
          .ToArray();

      AlignmentAction[,] actions =
        Align(existing, incoming);

      var matches =
        BacktrackAlignment(
          existing,
          incoming,
          actions);

      foreach (var match in matches)
      {
        if (match.ExistingIndex >= 0 &&
            match.IncomingIndex >= 0)
        {
          SymbolCluster cluster =
            existing[match.ExistingIndex];
          IncomingSymbol symbol =
            incoming[match.IncomingIndex];

          if (cluster.Committed)
          {
            // Stable prefix is immutable. Matching a later overlapping window
            // merely consumes that symbol so it cannot create a duplicate.
            cluster.LastObservedWindowEndUtc =
              windowEndUtc;
            continue;
          }

          AddVote(
            cluster,
            symbol,
            windowId,
            windowEndUtc);
        }
        else if (match.IncomingIndex >= 0)
        {
          IncomingSymbol symbol =
            incoming[match.IncomingIndex];
          var cluster = new SymbolCluster
          {
            Id = nextClusterId++,
            TimestampUtc =
              symbol.TimestampUtc,
            TimeWeight =
              Math.Max(
                symbol.Confidence,
                0.05),
            LastObservedWindowEndUtc =
              windowEndUtc
          };
          state.Provisional.Add(cluster);
          AddVote(
            cluster,
            symbol,
            windowId,
            windowEndUtc);
        }
      }

      state.Provisional.Sort((a, b) =>
      {
        int cmp =
          a.TimestampUtc.CompareTo(
            b.TimestampUtc);
        return cmp != 0
          ? cmp
          : a.Id.CompareTo(b.Id);
      });
    }

    private AlignmentAction[,] Align(
      IReadOnlyList<SymbolCluster> existing,
      IReadOnlyList<IncomingSymbol> incoming)
    {
      int m = existing.Count;
      int n = incoming.Count;
      double[,] cost =
        new double[m + 1, n + 1];
      var action =
        new AlignmentAction[m + 1, n + 1];

      const double SkipExistingCost = 0.72;
      const double InsertIncomingCost = 1.02;

      for (int i = 1; i <= m; i++)
      {
        cost[i, 0] =
          cost[i - 1, 0] +
          SkipExistingCost;
        action[i, 0] =
          AlignmentAction.SkipExisting;
      }

      for (int j = 1; j <= n; j++)
      {
        cost[0, j] =
          cost[0, j - 1] +
          InsertIncomingCost;
        action[0, j] =
          AlignmentAction.InsertIncoming;
      }

      double tolerance =
        options.SymbolMatchToleranceSeconds;

      for (int i = 1; i <= m; i++)
      {
        for (int j = 1; j <= n; j++)
        {
          double best =
            cost[i - 1, j] +
            SkipExistingCost;
          AlignmentAction bestAction =
            AlignmentAction.SkipExisting;

          double insert =
            cost[i, j - 1] +
            InsertIncomingCost;
          if (insert < best)
          {
            best = insert;
            bestAction =
              AlignmentAction.InsertIncoming;
          }

          double dt =
            Math.Abs(
              (existing[i - 1].TimestampUtc -
               incoming[j - 1].TimestampUtc)
              .TotalSeconds);

          if (dt <= tolerance)
          {
            char existingChar =
              WinningCharacter(
                existing[i - 1]).Character;
            double mismatch =
              existingChar ==
                incoming[j - 1].Character
                ? 0
                : 0.55;

            // A committed cluster may not be rewritten, but consuming a
            // conflicting symbol from a heavily overlapping window is still
            // preferable to manufacturing a duplicate character.
            if (existing[i - 1].Committed &&
                existingChar !=
                  incoming[j - 1].Character)
              mismatch += 0.10;

            double match =
              cost[i - 1, j - 1] +
              dt / tolerance +
              mismatch;

            if (match <= best)
            {
              best = match;
              bestAction =
                AlignmentAction.Match;
            }
          }

          cost[i, j] = best;
          action[i, j] = bestAction;
        }
      }

      return action;
    }

    private readonly record struct AlignmentPair(
      int ExistingIndex,
      int IncomingIndex);

    private static IReadOnlyList<AlignmentPair>
      BacktrackAlignment(
        IReadOnlyList<SymbolCluster> existing,
        IReadOnlyList<IncomingSymbol> incoming,
        AlignmentAction[,] actions)
    {
      int i = existing.Count;
      int j = incoming.Count;
      var reversed =
        new List<AlignmentPair>();

      while (i > 0 || j > 0)
      {
        AlignmentAction action =
          actions[i, j];

        switch (action)
        {
          case AlignmentAction.Match:
            reversed.Add(new(
              i - 1, j - 1));
            i--;
            j--;
            break;

          case AlignmentAction.SkipExisting:
            i--;
            break;

          case AlignmentAction.InsertIncoming:
            reversed.Add(new(
              -1, j - 1));
            j--;
            break;

          default:
            if (j > 0)
            {
              reversed.Add(new(
                -1, j - 1));
              j--;
            }
            else
            {
              i--;
            }
            break;
        }
      }

      reversed.Reverse();
      return reversed;
    }

    private static void AddVote(
      SymbolCluster cluster,
      IncomingSymbol symbol,
      long windowId,
      DateTime windowEndUtc)
    {
      if (!cluster.WindowIds.Add(windowId))
        return;

      if (!cluster.Votes.TryGetValue(
            symbol.Character,
            out Vote? vote))
      {
        vote = new Vote();
        cluster.Votes.Add(
          symbol.Character,
          vote);
      }

      double weight =
        Math.Max(
          symbol.Confidence,
          0.05);
      vote.Weight += weight;
      vote.Confirmations++;
      vote.ConfidenceSum +=
        symbol.Confidence;

      double combinedWeight =
        cluster.TimeWeight + weight;
      double oldTicks =
        cluster.TimestampUtc.Ticks;
      double newTicks =
        symbol.TimestampUtc.Ticks;
      long blendedTicks =
        (long)Math.Round(
          (oldTicks *
             cluster.TimeWeight +
           newTicks * weight) /
          Math.Max(
            combinedWeight,
            1e-12));

      cluster.TimestampUtc =
        new DateTime(
          blendedTicks,
          DateTimeKind.Utc);
      cluster.TimeWeight =
        combinedWeight;
      cluster.LastObservedWindowEndUtc =
        windowEndUtc;
    }

    private void CommitStablePrefix(
      LaneState state,
      DateTime newestWindowEndUtc)
    {
      DateTime commitCutoff =
        newestWindowEndUtc -
        TimeSpan.FromSeconds(
          options.CommitLagSeconds);
      DateTime abandonCutoff =
        newestWindowEndUtc -
        TimeSpan.FromSeconds(
          options.AbandonAfterSeconds);

      while (state.Provisional.Count > 0)
      {
        SymbolCluster cluster =
          state.Provisional[0];

        if (cluster.TimestampUtc >
            commitCutoff)
          break;

        (
          char character,
          double consensus,
          double averageConfidence,
          int confirmations) =
            WinningCharacter(cluster);

        bool ready =
          confirmations >=
            options.MinimumConfirmations &&
          consensus >=
            options.MinimumConsensus &&
          averageConfidence >=
            options.MinimumAverageConfidence;

        if (ready)
        {
          cluster.Committed = true;
          cluster.CommittedCharacter =
            character;
          state.Committed.Append(character);
          state.Provisional.RemoveAt(0);
          state.RecentCommitted.Add(cluster);
          continue;
        }

        if (cluster.TimestampUtc <=
            abandonCutoff)
        {
          // A very old one-window hallucination may not block the entire
          // stable prefix forever. Discard it once its correction horizon has
          // expired.
          state.Provisional.RemoveAt(0);
          continue;
        }

        // Stable-prefix semantics: never commit a newer symbol around an older
        // unresolved symbol. Wait for more overlapping evidence.
        break;
      }
    }

    private void PruneCommittedMatchHistory(
      LaneState state,
      DateTime newestWindowEndUtc)
    {
      DateTime cutoff =
        newestWindowEndUtc -
        TimeSpan.FromSeconds(
          options.CommittedMatchRetentionSeconds);
      state.RecentCommitted.RemoveAll(
        x => x.TimestampUtc < cutoff);
    }

    private static (
      char Character,
      double Consensus,
      double AverageConfidence,
      int Confirmations)
      WinningCharacter(
        SymbolCluster cluster)
    {
      if (cluster.Committed)
      {
        return (
          cluster.CommittedCharacter,
          1,
          1,
          int.MaxValue);
      }

      if (cluster.Votes.Count == 0)
        return ('?', 0, 0, 0);

      KeyValuePair<char, Vote> winner =
        cluster.Votes
          .OrderByDescending(
            x => x.Value.Weight)
          .ThenByDescending(
            x => x.Value.Confirmations)
          .ThenBy(x => x.Key)
          .First();

      double totalWeight =
        cluster.Votes.Values.Sum(
          x => x.Weight);
      double consensus =
        winner.Value.Weight /
        Math.Max(
          totalWeight,
          1e-12);
      double averageConfidence =
        winner.Value.ConfidenceSum /
        Math.Max(
          winner.Value.Confirmations,
          1);

      return (
        winner.Key,
        consensus,
        averageConfidence,
        winner.Value.Confirmations);
    }

    private static LaneKey IdentityKey(
      DeepCwLaneResult result) =>
      result.AssociationHintId != 0
        ? new(
            true,
            result.AssociationHintId)
        : new(
            false,
            result.TrackId);

    private static CwTranscriptSnapshot Snapshot(
      LaneState state)
    {
      CwTranscriptSymbol[] provisional =
        state.Provisional
          .OrderBy(x => x.TimestampUtc)
          .Select(x =>
          {
            var winner =
              WinningCharacter(x);
            return new CwTranscriptSymbol(
              winner.Character,
              x.TimestampUtc,
              winner.AverageConfidence *
                winner.Consensus,
              winner.Confirmations,
              Committed: false);
          })
          .ToArray();

      return new(
        state.TrackId,
        state.AssociationHintId,
        state.Committed.ToString(),
        new string(
          provisional
            .Select(x => x.Character)
            .ToArray()),
        provisional,
        state.LastWindowEndUtc);
    }

    private static void ValidateResult(
      DeepCwLaneResult result)
    {
      if (result.WindowEndUtc.Kind !=
          DateTimeKind.Utc)
        throw new ArgumentException(
          "CW transcript windows require UTC timestamps.",
          nameof(result));
      if (!double.IsFinite(
            result.WindowDurationSeconds) ||
          result.WindowDurationSeconds <= 0)
        throw new ArgumentException(
          "CW transcript requires a positive decode-window duration.",
          nameof(result));
      if (result.OutputFrameCount < 0)
        throw new ArgumentException(
          "CW transcript output-frame count is invalid.",
          nameof(result));
    }

    private static void ValidateOptions(
      CwTranscriptOptions value)
    {
      if (!double.IsFinite(
            value.SymbolMatchToleranceSeconds) ||
          value.SymbolMatchToleranceSeconds <= 0 ||
          value.SymbolMatchToleranceSeconds > 1.5)
        throw new ArgumentOutOfRangeException(
          nameof(value.SymbolMatchToleranceSeconds));
      if (!double.IsFinite(
            value.CommitLagSeconds) ||
          value.CommitLagSeconds < 0 ||
          value.CommitLagSeconds > 10)
        throw new ArgumentOutOfRangeException(
          nameof(value.CommitLagSeconds));
      if (!double.IsFinite(
            value.AbandonAfterSeconds) ||
          value.AbandonAfterSeconds <=
            value.CommitLagSeconds)
        throw new ArgumentOutOfRangeException(
          nameof(value.AbandonAfterSeconds));
      if (value.MinimumConfirmations is < 1 or > 8)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumConfirmations));
      if (value.MinimumConsensus <= 0.5 ||
          value.MinimumConsensus > 1)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumConsensus));
      if (value.MinimumAverageConfidence < 0 ||
          value.MinimumAverageConfidence > 1)
        throw new ArgumentOutOfRangeException(
          nameof(value.MinimumAverageConfidence));
      if (!double.IsFinite(
            value.CommittedMatchRetentionSeconds) ||
          value.CommittedMatchRetentionSeconds <
            value.SymbolMatchToleranceSeconds)
        throw new ArgumentOutOfRangeException(
          nameof(value.CommittedMatchRetentionSeconds));
    }
  }
}
