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
    int Transitions, int OnRuns,
    double FskOffDominance = 0,
    double FskOnDominance = 0,
    int FskOffFrames = 0);

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
      // Detect a true switching *dominant frequency*, not just any
      // sideband energy: FFT sidelobes exist even while the original
      // CW tone is keyed down.
      double[] bestFrequency=new double[count];
      double[] bestSnr=new double[count];
      Array.Fill(bestSnr,double.NegativeInfinity);
      for(int i=0;i<count;i++)bestFrequency[i]=double.NaN;
      foreach(CwRidgeObservation obs in scan.FastObservations)
      {
        long relative=obs.CenterSampleIndex-(start+win/2);
        int i=(int)Math.Round(relative/(double)hop);
        if(i<0 || i>=count)continue;
        double dt=(obs.CenterSampleIndex-snapshot.EndSampleIndex)/
          (double)snapshot.SampleRate;
        double center=track.FrequencyHz+track.DriftHzPerSecond*dt;
        if(Math.Abs(obs.FrequencyHz-center)>220)continue;
        if(obs.SnrDb>bestSnr[i])
        {
          bestSnr[i]=obs.SnrDb;
          bestFrequency[i]=obs.FrequencyHz;
        }
      }
      // Spectral dominance alone is insufficient: randomly varying noise
      // peaks also dominate some CW key-up frames. A hopping modulation
      // repeatedly occupies the SAME alternate frequency during target OFF.
      int on=0,off=0;
      double binHz=snapshot.SampleRate/(double)win;
      double separation=Math.Max(10.0,0.9*binHz);
      var switchedOff=new List<double>();
      for(int i=0;i<count;i++)
      {
        double dt=(start+win/2+(long)i*hop-
          snapshot.EndSampleIndex)/(double)snapshot.SampleRate;
        double center=track.FrequencyHz+
          track.DriftHzPerSecond*dt;
        bool targetOn=snr[i]>=threshold;
        if(targetOn){on++;continue;}
        off++;
        if(bestSnr[i]>=scanner.Options.FastMinimumSnrDb+4 &&
           double.IsFinite(bestFrequency[i]) &&
           Math.Abs(bestFrequency[i]-center)>=separation)
          switchedOff.Add(bestFrequency[i]);
      }

      double modeFrequency=double.NaN;
      int modeCount=0;
      double tolerance=1.5*binHz;
      foreach(double candidate in switchedOff)
      {
        int amount=switchedOff.Count(f=>
          Math.Abs(f-candidate)<=tolerance);
        if(amount>modeCount)
        {
          modeCount=amount;
          modeFrequency=candidate;
        }
      }
      // Independent CW stations often coexist in the same narrow band.
      // Only classify as frequency hopping if the modal alternate tone
      // is essentially absent while the target tone is on.
      var alternateOnFrames=new HashSet<int>();
      if(double.IsFinite(modeFrequency))
      {
        foreach(CwRidgeObservation obs in scan.FastObservations)
        {
          long relative=obs.CenterSampleIndex-(start+win/2);
          int i=(int)Math.Round(relative/(double)hop);
          if(i<0 || i>=count || snr[i]<threshold)continue;
          if(Math.Abs(obs.FrequencyHz-modeFrequency)<=tolerance &&
             obs.SnrDb>=scanner.Options.FastMinimumSnrDb+8)
            alternateOnFrames.Add(i);
        }
      }
      double offRatio=modeCount/(double)Math.Max(1,off);
      double onRatio=alternateOnFrames.Count/(double)Math.Max(1,on);
      bool hopping=on>=4 && off>=4 && modeCount>=6 &&
        offRatio>=0.45 && onRatio<=0.30;
      return result with
      {
        IsKeyed=!hopping,
        FskOffDominance=offRatio,
        FskOnDominance=onRatio,
        FskOffFrames=off
      };
    }

    private static double Quantile(double[] sorted,double q)
    {
      double pos=(sorted.Length-1)*q;
      int lo=(int)pos,hi=Math.Min(sorted.Length-1,lo+1);
      return sorted[lo]+(sorted[hi]-sorted[lo])*(pos-lo);
    }
  }
}
