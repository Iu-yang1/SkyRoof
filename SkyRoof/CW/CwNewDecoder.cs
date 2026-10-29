namespace SkyRoof.CW
{
  /// <summary>
  /// The only user-facing receive decoder selection. DeepCW uses the
  /// installed ONNX CTC model. NewCW is a complete offline receive path:
  /// shared STFT/Kalman tracking -> temporal keyed-station/phase evidence ->
  /// bounded multi-tone coherent separation -> local Morse HSMM ->
  /// timestamped consensus and persistent transcript archive.
  /// </summary>
  public enum CwDecoderMode
  {
    DeepCW = 0,
    NewCW = 1
  }

  /// <summary>
  /// Standalone, no-model multi-lane Morse decoder. The shared tracker
  /// supplies frequency, drift, identity and verified CW evidence. This
  /// method has no model download, ONNX, web connection or TX dependency.
  /// </summary>
  public sealed class CwNewDecoder
  {
    public int MaxLanes {get;}
    public const double MinimumTextEvidence = 0.80;

    public CwNewDecoder(int maxLanes = 8)
    {
      if(maxLanes is < 1 or > 8)
        throw new ArgumentOutOfRangeException(nameof(maxLanes));
      MaxLanes=maxLanes;
    }

    public IReadOnlyList<DeepCwLaneResult> Decode(
      ReadOnlySpan<float> pcm,int sampleRate,DateTime windowEndUtc,
      IEnumerable<CwSignalTrack> tracks)
    {
      if(sampleRate<2000)
        throw new ArgumentOutOfRangeException(nameof(sampleRate));
      if(windowEndUtc.Kind!=DateTimeKind.Utc)
        throw new ArgumentException("New CW requires UTC timestamps.",
          nameof(windowEndUtc));
      ArgumentNullException.ThrowIfNull(tracks);
      if(pcm.Length<sampleRate/2)return [];

      CwSignalTrack[] accepted=tracks
        .Where(t=>t.Confirmed && t.Active &&
          t.KeyingStatus==CwKeyingStatus.Verified &&
          double.IsFinite(t.FrequencyHz) &&
          t.FrequencyHz>90 && t.FrequencyHz<sampleRate*0.4)
        .GroupBy(t=>t.AssociationHintId!=0
          ? (true,t.AssociationHintId):(false,t.Id))
        .Select(g=>g.OrderByDescending(t=>t.SnrDb).First())
        .OrderByDescending(t=>t.SnrDb)
        .Take(MaxLanes)
        .OrderBy(t=>t.FrequencyHz)
        .ToArray();
      if(accepted.Length==0)return [];

      // A local least-squares separation is attempted only when two or
      // more keyed carriers are close enough to interfere. Distant carriers
      // retain the raw PCM to avoid fitting irrelevant narrowband clutter.
      var isolated=new Dictionary<int,float[]>();
      for(int i=0;i<accepted.Length;)
      {
        int end=i+1;
        while(end<accepted.Length && end-i<4 &&
          accepted[end].FrequencyHz-accepted[end-1].FrequencyHz<=55)
          end++;
        CwSignalTrack[] group=accepted[i..end];
        if(group.Length>=2 &&
          CwCoherentMultitoneSeparator.TrySeparate(
            pcm,sampleRate,group,out var split))
        {
          foreach(var item in split)isolated[item.Key]=item.Value;
        }
        i=end;
      }

      var results=new List<DeepCwLaneResult>(accepted.Length);
      double duration=pcm.Length/(double)sampleRate;
      foreach(CwSignalTrack track in accepted)
      {
        ReadOnlySpan<float> laneAudio=
          isolated.TryGetValue(track.Id,out float[]? source)
          ? source : pcm;

        // When nearby carriers cannot be independently fitted, assigning
        // the same audio to both stations would manufacture a second
        // station's text. Suppress that ambiguous lane instead.
        bool unresolved=accepted.Any(other=>other.Id!=track.Id &&
          Math.Abs(other.FrequencyHz-track.FrequencyHz)<9) &&
          !isolated.ContainsKey(track.Id);

        float[] activity=unresolved ? [] :
          CwHsmmDecoder.EstimateEnvelope(
            laneAudio,sampleRate,track.FrequencyHz);
        CwHsmmResult morse=activity.Length<20
          ? new("",0,0,activity.Length,[])
          : CwHsmmDecoder.Decode(activity,0.010);

        // No optimistic auto-text on noise/ambiguous evidence. Timing
        // score is not calibrated BER or a true posterior probability.
        bool acceptText=!unresolved &&
          morse.EvidenceScore>=MinimumTextEvidence &&
          morse.Text.Length>=2;
        var symbols=acceptText
          ? morse.Symbols.Select(x=>
             new DeepCwDecodedSymbol(
              x.Character,Math.Clamp(x.Frame,0,
                Math.Max(0,morse.InputFrames-1)),
              morse.EvidenceScore)).ToArray()
          : [];
        results.Add(new DeepCwLaneResult(
          track.Id,track.FrequencyHz,acceptText?morse.Text:"",
          track.Active,unresolved || track.Ambiguous,windowEndUtc,
          track.AssociationHintId,symbols,morse.InputFrames,duration));
      }
      return results;
    }
  }

  internal sealed class NewCwInferenceSession : ICwInferenceSession
  {
    private readonly CwNewDecoder decoder;
    private readonly CwIncrementalTranscriptCoordinator transcript;

    public NewCwInferenceSession(CwReceiveWorkerOptions options)
    {
      decoder=new CwNewDecoder(options.MaxDecodeLanes);
      transcript=new CwIncrementalTranscriptCoordinator(
        new CwTranscriptOptions{MinimumEvidenceSpanSeconds=2.0});
    }

    public CwContinuousDecodeBatch Decode(
      CwAudioSnapshot snapshot,IReadOnlyList<CwSignalTrack> tracks)
    {
      IReadOnlyList<DeepCwLaneResult> lanes=decoder.Decode(
        snapshot.Samples,snapshot.SampleRate,snapshot.EndUtc,tracks);
      return new CwContinuousDecodeBatch(lanes,transcript.Push(lanes));
    }

    public void Reset()=>transcript.Reset();
    public void Dispose(){}
  }
}
