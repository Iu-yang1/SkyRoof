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
