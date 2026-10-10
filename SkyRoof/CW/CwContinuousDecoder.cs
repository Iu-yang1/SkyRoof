namespace SkyRoof.CW
{
  public sealed class CwContinuousDecodeBatch
  {
    public IReadOnlyList<DeepCwLaneResult> LaneResults { get; }
    public IReadOnlyList<CwTranscriptSnapshot> Transcripts { get; }

    public CwContinuousDecodeBatch(
      IReadOnlyList<DeepCwLaneResult> laneResults,
      IReadOnlyList<CwTranscriptSnapshot> transcripts)
    {
      LaneResults = laneResults;
      Transcripts = transcripts;
    }
  }

  /// <summary>
  /// Small receive-side orchestration layer for the future CW Console:
  /// separated multi-lane DeepCW inference followed by time-aligned
  /// incremental transcript reconciliation.
  ///
  /// It deliberately owns no audio device, UI, or transmitter state. Those
  /// remain outside the decode core and can feed deterministic PCM windows to
  /// this class.
  /// </summary>
  public sealed class CwContinuousDeepCwDecoder
  {
    public DeepCwMultiLaneDecoder Decoder { get; }
    public CwIncrementalTranscriptCoordinator Transcripts { get; }

    public CwContinuousDeepCwDecoder(
      DeepCwMultiLaneDecoder decoder,
      CwIncrementalTranscriptCoordinator? transcripts = null)
    {
      Decoder = decoder ??
        throw new ArgumentNullException(nameof(decoder));
      Transcripts =
        transcripts ??
        new CwIncrementalTranscriptCoordinator();
    }

    public CwContinuousDecodeBatch Decode(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DateTime windowEndUtc,
      IEnumerable<CwSignalTrack> tracks) =>
      Decode(audio, sourceSampleRate, windowEndUtc, tracks, null);

    internal CwContinuousDecodeBatch Decode(
      ReadOnlySpan<float> audio,
      int sourceSampleRate,
      DateTime windowEndUtc,
      IEnumerable<CwSignalTrack> tracks,
      long? endSampleIndex)
    {
      IReadOnlyList<DeepCwLaneResult> lanes =
        Decoder.Decode(
          audio,
          sourceSampleRate,
          windowEndUtc,
          tracks,
          endSampleIndex);

      IReadOnlyList<CwTranscriptSnapshot> transcript =
        Transcripts.Push(lanes);

      return new(
        lanes,
        transcript);
    }

    public void Reset()
    {
      Transcripts.Reset();
      Decoder.ResetFeatureCache();
    }
  }
}
