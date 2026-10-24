namespace SkyRoof.CW
{
  public enum CwKeyingStatus : byte
  {
    Unknown,   // Standalone/injected tracker: use independent physical gate.
    Candidate, // Narrowband ridge; no sufficiently keyed CW evidence yet.
    Verified
  }

  public readonly record struct CwKeyingAssessment(
    bool IsKeyed, double KeyingDepthDb, double DutyCycle,
    int Transitions, int OnRuns);

  /// <summary>
  /// Evidence of *keying*, as opposed to evidence of a stable spectral ridge.
  /// Uses the scanner's already computed short-window spectral measurements
  /// and includes missing frames. Does not add FFTs to the tracking loop.
  /// </summary>
  public static class CwKeyingGate
  {
    public static CwKeyingAssessment AssessSnrSeries(
      ReadOnlySpan<double> snrDb, double hopSeconds,
      double minimumKeyingDepthDb = 6.0)
    {
      if (snrDb.Length < 12 || !double.IsFinite(hopSeconds) ||
          hopSeconds <= 0)
        return default;
      double[] ordered = snrDb.ToArray();
      Array.Sort(ordered);
      double lo = Quantile(ordered,0.15);
      double hi = Quantile(ordered,0.85);
      double depth = hi-lo;
      if (!double.IsFinite(depth) || depth < minimumKeyingDepthDb)
        return new(false,Math.Max(0,depth),0,0,0);
      double threshold=lo+0.45*depth;
      bool[] state=new bool[snrDb.Length];
      for(int i=0;i<state.Length;i++)
        state[i]=snrDb[i]>=threshold;
      int active=0,transitions=0,onRuns=0,offRuns=0;
      int onDuration=0,offDuration=0,validOnRuns=0,validOffRuns=0;
      for(int i=0;i<state.Length;i++)
      {
        if(state[i]){active++;onDuration++;}
        else offDuration++;
        if(i>0 && state[i]!=state[i-1])
        {
          transitions++;
          if(state[i-1])
          {
            onRuns++;
            if(onDuration*hopSeconds>=0.025)validOnRuns++;
            onDuration=0;
          }
          else
          {
            offRuns++;
            if(offDuration*hopSeconds>=0.025)validOffRuns++;
            offDuration=0;
          }
        }
      }
      if(state[^1])
      {
        onRuns++;
        if(onDuration*hopSeconds>=0.025)validOnRuns++;
      }
      else
      {
        offRuns++;
        if(offDuration*hopSeconds>=0.025)validOffRuns++;
      }
      double duty=active/(double)state.Length;
      bool keyed=duty>=0.08 && duty<=0.92 &&
        transitions>=3 && validOnRuns>=2 && validOffRuns>=1 &&
        transitions<=state.Length/2;
      return new(keyed,depth,duty,transitions,onRuns);
    }

    public static CwKeyingAssessment AssessRidge(
      CwSignalTrack track,CwFrameRidgeScanResult scan,
      CwAudioSnapshot snapshot,CwFrameRidgeScanner scanner)
    {
      int win=scanner.FastWindowSamples,hop=scanner.FastHopSamples;
      int count=1+(snapshot.Samples.Length-win)/hop;
      if(count<12)return default;
      double[] snr=new double[count];
      double minimum=scanner.Options.FastMinimumSnrDb;
      Array.Fill(snr,minimum-6.0);
      long start=snapshot.EndSampleIndex-snapshot.Samples.Length;
      double gateHz=Math.Max(10,0.9*snapshot.SampleRate/(double)win);
      foreach(CwRidgeObservation obs in scan.FastObservations)
      {
        long relative=obs.CenterSampleIndex-(start+win/2);
        int i=(int)Math.Round(relative/(double)hop);
        if(i<0 || i>=count)continue;
        double dt=(obs.CenterSampleIndex-snapshot.EndSampleIndex)/
          (double)snapshot.SampleRate;
        double expected=track.FrequencyHz+track.DriftHzPerSecond*dt;
        if(Math.Abs(obs.FrequencyHz-expected)>gateHz)continue;
        snr[i]=Math.Max(snr[i],obs.SnrDb);
      }
      CwKeyingAssessment result=AssessSnrSeries(
        snr,hop/(double)snapshot.SampleRate);
      if(!result.IsKeyed)return result;

      // FSK/MFSK can key *one spectral bin* without keying a transmitter.
      // Reject frequency hopping when neighboring tones occupy the exact
      // complementary OFF intervals but disappear during target ON intervals.
      // Independent CW neighbors normally have overlapping ON intervals.
      double[] ordered=snr.ToArray();
      Array.Sort(ordered);
      double low=Quantile(ordered,0.15);
      double high=Quantile(ordered,0.85);
      double threshold=low+0.45*(high-low);
      double[] other=new double[count];
      Array.Fill(other,double.NegativeInfinity);
      foreach(CwRidgeObservation obs in scan.FastObservations)
      {
        long relative=obs.CenterSampleIndex-(start+win/2);
        int i=(int)Math.Round(relative/(double)hop);
        if(i<0 || i>=count)continue;
        double dt=(obs.CenterSampleIndex-snapshot.EndSampleIndex)/
          (double)snapshot.SampleRate;
        double center=track.FrequencyHz+track.DriftHzPerSecond*dt;
        double distance=Math.Abs(obs.FrequencyHz-center);
        if(distance>=Math.Max(9,0.8*obs.ResolutionHz) &&
           distance<=220)
          other[i]=Math.Max(other[i],obs.SnrDb);
      }
      int on=0,off=0,onOther=0,offOther=0;
      double neighborThreshold=Math.Max(
        scanner.Options.FastMinimumSnrDb+4.0,high-9.0);
      for(int i=0;i<count;i++)
      {
        bool targetOn=snr[i]>=threshold;
        bool neighborOn=other[i]>=neighborThreshold;
        if(targetOn){on++;if(neighborOn)onOther++;}
        else {off++;if(neighborOn)offOther++;}
      }
      if(on>=4 && off>=4 &&
         offOther>=4 &&
         offOther/(double)off>=0.85 &&
         onOther/(double)on<=0.20)
        return result with { IsKeyed=false };
      return result;
    }

    private static double Quantile(double[] sorted,double q)
    {
      double pos=(sorted.Length-1)*q;
      int lo=(int)pos,hi=Math.Min(sorted.Length-1,lo+1);
      return sorted[lo]+(sorted[hi]-sorted[lo])*(pos-lo);
    }
  }
}
