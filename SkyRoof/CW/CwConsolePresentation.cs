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

      return transcript.CommittedText +
        " ⟦" +
        transcript.ProvisionalText +
        "⟧";
    }
  }
}
