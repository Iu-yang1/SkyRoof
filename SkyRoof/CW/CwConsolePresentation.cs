namespace SkyRoof.CW
{
  public readonly record struct CwConsoleLaneIdentity(
    bool UsesAssociationHint,
    int Id)
  {
    public override string ToString() =>
      UsesAssociationHint
        ? $"H{Id}"
        : $"T{Id}";
  }

  internal readonly record struct CwConsoleLaneSlot(
    int Index,
    CwConsoleLaneIdentity? Identity,
    CwSignalTrack? Track,
    bool Present);

  internal sealed class CwConsoleLaneSlotMap
  {
    private sealed class Entry
    {
      public required CwConsoleLaneIdentity Identity;
      public required int Slot;
      public required CwSignalTrack Track;
      public DateTime LastPresentUtc;
      public bool Present;
    }

    private readonly int maxSlots;
    private readonly TimeSpan releaseDelay;
    private readonly Dictionary<CwConsoleLaneIdentity, Entry> entries = new();

    internal CwConsoleLaneSlotMap(
      int maxSlots = 8,
      TimeSpan? releaseDelay = null)
    {
      if (maxSlots is < 1 or > 32)
        throw new ArgumentOutOfRangeException(nameof(maxSlots));
      this.maxSlots = maxSlots;
      this.releaseDelay =
        releaseDelay ??
        TimeSpan.FromSeconds(4);
      if (this.releaseDelay < TimeSpan.Zero)
        throw new ArgumentOutOfRangeException(nameof(releaseDelay));
    }

    internal int MaxSlots => maxSlots;

    internal IReadOnlyList<CwConsoleLaneSlot> Update(
      IEnumerable<CwSignalTrack> tracks,
      DateTime nowUtc)
    {
      ArgumentNullException.ThrowIfNull(tracks);
      if (nowUtc.Kind != DateTimeKind.Utc)
        throw new ArgumentException(
          "CW lane slot timestamps must be UTC.",
          nameof(nowUtc));

      foreach (Entry entry in entries.Values)
        entry.Present = false;

      CwSignalTrack[] visible =
        CwConsolePresentation
          .CollapseDuplicateLaneIdentities(tracks);

      foreach (CwSignalTrack track in visible)
      {
        CwConsoleLaneIdentity identity =
          CwConsolePresentation.Identity(track);
        if (entries.TryGetValue(identity, out Entry? entry))
        {
          entry.Track = track;
          entry.LastPresentUtc = nowUtc;
          entry.Present = true;
        }
      }

      CwConsoleLaneIdentity[] expired =
        entries.Values
          .Where(entry =>
            !entry.Present &&
            nowUtc - entry.LastPresentUtc >= releaseDelay)
          .Select(entry => entry.Identity)
          .ToArray();
      foreach (CwConsoleLaneIdentity identity in expired)
        entries.Remove(identity);

      foreach (CwSignalTrack track in visible)
      {
        CwConsoleLaneIdentity identity =
          CwConsolePresentation.Identity(track);
        if (entries.ContainsKey(identity))
          continue;

        int slot = FirstFreeSlot();
        if (slot < 0)
        {
          Entry? recyclable =
            entries.Values
              .Where(entry => !entry.Present)
              .OrderBy(entry => entry.LastPresentUtc)
              .FirstOrDefault();
          if (recyclable != null)
          {
            slot = recyclable.Slot;
            entries.Remove(recyclable.Identity);
          }
        }
        if (slot < 0)
          continue;

        entries[identity] =
          new Entry
          {
            Identity = identity,
            Slot = slot,
            Track = track,
            LastPresentUtc = nowUtc,
            Present = true
          };
      }

      var slots =
        Enumerable.Range(0, maxSlots)
          .Select(index =>
          {
            Entry? entry =
              entries.Values.FirstOrDefault(value =>
                value.Slot == index);
            return entry == null
              ? new CwConsoleLaneSlot(index, null, null, false)
              : new CwConsoleLaneSlot(
                  index,
                  entry.Identity,
                  entry.Track,
                  entry.Present);
          })
          .ToArray();

      return slots;
    }

    internal void Reset() => entries.Clear();

    private int FirstFreeSlot()
    {
      HashSet<int> used =
        entries.Values
          .Select(entry => entry.Slot)
          .ToHashSet();
      for (int i = 0; i < maxSlots; i++)
        if (!used.Contains(i))
          return i;
      return -1;
    }
  }

  internal static class CwConsolePresentation
  {
    internal static CwConsoleLaneIdentity Identity(
      CwSignalTrack track) =>
      track.AssociationHintId != 0
        ? new(
            true,
            track.AssociationHintId)
        : new(
            false,
            track.Id);

    internal static CwConsoleLaneIdentity Identity(
      CwTranscriptSnapshot transcript) =>
      transcript.AssociationHintId != 0
        ? new(
            true,
            transcript.AssociationHintId)
        : new(
            false,
            transcript.TrackId);

    internal static CwSignalTrack[] CollapseDuplicateLaneIdentities(
      IEnumerable<CwSignalTrack> tracks) =>
      tracks
        .GroupBy(Identity)
        .Select(group =>
          group
            .OrderBy(track => track.Ambiguous)
            .ThenByDescending(track => track.Active)
            .ThenByDescending(track => track.IdentityConfidence)
            .ThenByDescending(track => track.SnrDb)
            .ThenByDescending(track => track.LastSeenUtc)
            .First())
        .OrderBy(track => track.FrequencyHz)
        .ToArray();

    internal static string LaneLabel(
      CwSignalTrack track) =>
      Identity(track).ToString();

    internal static string LaneDiagnosticLabel(
      CwSignalTrack track)
    {
      CwConsoleLaneIdentity identity =
        Identity(track);

      return identity.UsesAssociationHint
        ? $"{identity} · #{track.Id}"
        : identity.ToString();
    }

    internal static string StateText(
      CwSignalTrack track)
    {
      if (track.Ambiguous)
        return "Ambiguous";
      if (track.Active)
        return "Active";
      return "Hold";
    }

    internal static string GridTranscript(
      CwTranscriptSnapshot? transcript)
    {
      if (transcript == null)
        return string.Empty;

      if (string.IsNullOrEmpty(
            transcript.ProvisionalText))
        return transcript.CommittedText;

      // Preserve decoded whitespace exactly. The bracket is UI chrome,
      // not text content, so never inject or trim a semantic CW space here.
      return transcript.CommittedText +
        "⟦" +
        transcript.ProvisionalText +
        "⟧";
    }
  }
}
