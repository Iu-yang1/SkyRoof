using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  /// <summary>A peak measured by a CW detector, in audio-frequency Hz.</summary>
  public readonly record struct CwSignalCandidate(double FrequencyHz, double SnrDb);

  /// <summary>
  /// Stable identity and observed state of one independently decodable CW lane.
  /// The tracker identifies carriers; it does not claim to decode Morse symbols.
  /// </summary>
  public readonly record struct CwSignalTrack(
    int Id,
    double FrequencyHz,
    double SnrDb,
    double DriftHzPerSecond,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    bool Confirmed,
    bool Active);

  /// <summary>
  /// Tracks simultaneous CW carrier candidates across detector updates. IDs are stable
  /// during Doppler drift/QSB, and short fades do not immediately delete a lane.
  /// Detection, frequency isolation and per-lane neural inference belong to separate
  /// stages of the CW Console pipeline.
  /// </summary>
  public sealed class CwPileupTrackManager
  {
    private sealed class State
    {
      public int Id;
      public double FrequencyHz;
      public double SnrDb;
      public double DriftHzPerSecond;
      public DateTime FirstSeenUtc;
      public DateTime LastSeenUtc;
      public int SeenCount;
    }

    private readonly List<State> tracks = new();
    private DateTime? previousUpdateUtc;
    private int nextId = 1;

    public int MaxTracks { get; }
    public double MinimumSeparationHz { get; }
    public double MatchToleranceHz { get; }
    public TimeSpan ConfirmationDelay { get; }
    public TimeSpan HoldTime { get; }

    public CwPileupTrackManager(
      int maxTracks = 8,
      double minimumSeparationHz = 25,
      double matchToleranceHz = 40,
      TimeSpan? confirmationDelay = null,
      TimeSpan? holdTime = null)
    {
      if (maxTracks is < 1 or > 32)
        throw new ArgumentOutOfRangeException(nameof(maxTracks));
      if (!double.IsFinite(minimumSeparationHz) || minimumSeparationHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(minimumSeparationHz));
      if (!double.IsFinite(matchToleranceHz) || matchToleranceHz <= 0)
        throw new ArgumentOutOfRangeException(nameof(matchToleranceHz));

      MaxTracks = maxTracks;
      MinimumSeparationHz = minimumSeparationHz;
      MatchToleranceHz = matchToleranceHz;
      ConfirmationDelay = confirmationDelay ?? TimeSpan.FromMilliseconds(400);
      HoldTime = holdTime ?? TimeSpan.FromSeconds(5);

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

      // Prefer strong candidates, then suppress duplicate spectral peaks. The
      // input list may contain spurious NaN/Inf bins from an external detector.
      var clean = new List<CwSignalCandidate>();
      foreach (var candidate in candidates
        .Where(c => double.IsFinite(c.FrequencyHz) &&
                    double.IsFinite(c.SnrDb) && c.FrequencyHz > 0)
        .OrderByDescending(c => c.SnrDb)
        .ThenBy(c => c.FrequencyHz))
      {
        if (clean.All(c => Math.Abs(c.FrequencyHz - candidate.FrequencyHz)
              >= MinimumSeparationHz))
          clean.Add(candidate);
      }

      // Associate by predicted frequency, not simply descending signal power.
      // Strong and weak CW stations must retain their independent track IDs.
      var possiblePairs = new List<(double Distance, int Track, int Peak)>();
      for (int i = 0; i < tracks.Count; i++)
      {
        State track = tracks[i];
        double elapsed = (utc - track.LastSeenUtc).TotalSeconds;
        double predicted = track.FrequencyHz +
          track.DriftHzPerSecond * Math.Min(elapsed, 2.0);
        for (int j = 0; j < clean.Count; j++)
        {
          double distance = Math.Abs(clean[j].FrequencyHz - predicted);
          if (distance <= MatchToleranceHz)
            possiblePairs.Add((distance, i, j));
        }
      }

      var usedTracks = new HashSet<int>();
      var usedPeaks = new HashSet<int>();
      foreach (var pair in possiblePairs
        .OrderBy(p => p.Distance).ThenBy(p => tracks[p.Track].Id))
      {
        if (!usedTracks.Add(pair.Track)) continue;
        if (!usedPeaks.Add(pair.Peak))
        {
          usedTracks.Remove(pair.Track);
          continue;
        }

        State track = tracks[pair.Track];
        CwSignalCandidate peak = clean[pair.Peak];
        double seconds = (utc - track.LastSeenUtc).TotalSeconds;
        if (seconds > 0)
        {
          double rate = (peak.FrequencyHz - track.FrequencyHz) / seconds;
          // Avoid extreme rate excursions when a nearby interferer appears.
          rate = Math.Clamp(rate, -60, 60);
          track.DriftHzPerSecond =
            0.75 * track.DriftHzPerSecond + 0.25 * rate;
        }

        track.FrequencyHz =
          0.3 * track.FrequencyHz + 0.7 * peak.FrequencyHz;
        track.SnrDb = peak.SnrDb;
        track.LastSeenUtc = utc;
        track.SeenCount++;
      }

      for (int j = 0; j < clean.Count && tracks.Count < MaxTracks; j++)
      {
        if (usedPeaks.Contains(j)) continue;
        CwSignalCandidate peak = clean[j];
        if (tracks.Any(t => Math.Abs(t.FrequencyHz - peak.FrequencyHz)
              < MinimumSeparationHz))
          continue;

        tracks.Add(new State
        {
          Id = nextId++,
          FrequencyHz = peak.FrequencyHz,
          SnrDb = peak.SnrDb,
          FirstSeenUtc = utc,
          LastSeenUtc = utc,
          SeenCount = 1
        });
      }

      return tracks.OrderBy(t => t.FrequencyHz).Select(t =>
        new CwSignalTrack(
          t.Id, t.FrequencyHz, t.SnrDb, t.DriftHzPerSecond,
          t.FirstSeenUtc, t.LastSeenUtc,
          t.SeenCount >= 2 && utc - t.FirstSeenUtc >= ConfirmationDelay,
          t.LastSeenUtc == utc)).ToArray();
    }
  }
}
