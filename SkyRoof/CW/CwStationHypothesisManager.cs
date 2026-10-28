namespace SkyRoof.CW
{
  /// <summary>
  /// Experimental, deterministic evidence accumulator for a true keyed
  /// station rather than an isolated spectral peak. The result is an
  /// uncalibrated hypothesis score, NOT a Bayesian posterior probability.
  /// Only independently timestamped calls advance a track's evidence.
  /// </summary>
  public sealed class CwStationHypothesisManager
  {
    private sealed class State
    {
      public DateTime LastUtc;
      public double Evidence;
      public int KeyedObservations;
      public bool Admitted;
    }
    private readonly Dictionary<int,State> states=new();

    public bool Update(
      CwSignalTrack track, CwKeyingAssessment keying, DateTime utc)
    {
      if(utc.Kind!=DateTimeKind.Utc)
        throw new ArgumentException("Expected UTC.",nameof(utc));
      if(!states.TryGetValue(track.Id,out State? state))
      {
        state=new State{LastUtc=utc,Evidence=0};
        states.Add(track.Id,state);
      }
      if(utc<=state.LastUtc && state.KeyedObservations>0)
        return state.Admitted;
      double dt=Math.Clamp((utc-state.LastUtc).TotalSeconds,0,0.25);
      if(state.KeyedObservations==0)dt=Math.Max(dt,0.12);
      state.LastUtc=utc;
      double decay=Math.Exp(-dt/1.5);
      state.Evidence*=decay;
      bool supported=track.Confirmed && keying.IsKeyed &&
        keying.Transitions>=3 && keying.DutyCycle>=0.08 &&
        keying.DutyCycle<=0.92 &&
        keying.KeyingDepthDb>=6.0;
      if(supported)
      {
        state.KeyedObservations++;
        state.Evidence+=dt*Math.Clamp(
          1.0+(track.SnrDb-7.0)/12.0,0.5,2.0);
      }
      else
        state.Evidence-=dt*1.3;
      state.Evidence=Math.Clamp(state.Evidence,-2,3);
      if(!state.Admitted && state.KeyedObservations>=4 &&
         state.Evidence>=0.55)state.Admitted=true;
      else if(state.Admitted && state.Evidence<=-0.5)
        state.Admitted=false;
      foreach(int id in states.Where(x=>utc-x.Value.LastUtc>
          TimeSpan.FromSeconds(12)).Select(x=>x.Key).ToArray())
        states.Remove(id);
      return state.Admitted;
    }

    public void Reset()=>states.Clear();
  }
}
