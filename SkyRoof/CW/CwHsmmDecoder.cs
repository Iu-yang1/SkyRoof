namespace SkyRoof.CW
{
  public sealed record CwHsmmSymbol(char Character,int Frame);
  public sealed record CwHsmmResult(
    string Text,double EvidenceScore,double Wpm,
    int InputFrames,IReadOnlyList<CwHsmmSymbol> Symbols);

  /// <summary>
  /// Experimental Morse semi-Markov duration decoder. Key-down/key-up
  /// observations are soft (0..1). Runs are latent dot/dash, inter-element,
  /// character and word durations, with a WPM duration prior and a lexicon.
  /// No assumption that the evidence score is a calibrated probability.
  /// </summary>
  public static class CwHsmmDecoder
  {
    private static readonly Dictionary<string,char> Morse=new()
    {
      [".-"]='A',["-..."]='B',["-.-."]='C',["-.."]='D',
      ["."]='E',["..-."]='F',["--."]='G',["...."]='H',
      [".."]='I',[".---"]='J',["-.-"]='K',[".-.."]='L',
      ["--"]='M',["-."]='N',["---"]='O',[".--."]='P',
      ["--.-"]='Q',[".-."]='R',["..."]='S',["-"]='T',
      ["..-"]='U',["...-"]='V',[".--"]='W',["-..-"]='X',
      ["-.--"]='Y',["--.."]='Z',
      ["-----"]='0',[".----"]='1',["..---"]='2',
      ["...--"]='3',["....-"]='4',["....."]='5',
      ["-...."]='6',["--..."]='7',["---.."]='8',
      ["----."]='9',["..--.."]='?',["-..-."]='/'
    };

    private readonly record struct Run(bool On,int Start,int Length,double Mean);
    public static CwHsmmResult Decode(
      ReadOnlySpan<float> keyDown,double secondsPerFrame,
      int minWpm=8,int maxWpm=40)
    {
      if(secondsPerFrame<=0 || !double.IsFinite(secondsPerFrame) ||
        minWpm<5 || maxWpm>60 || minWpm>maxWpm)
        throw new ArgumentOutOfRangeException(nameof(secondsPerFrame));
      if(keyDown.Length<20)
        return new("",0,0,keyDown.Length,[]);
      var runs=new List<Run>();
      bool current=keyDown[0]>=0.5;
      int start=0;
      double sum=0;
      for(int i=0;i<=keyDown.Length;i++)
      {
        bool next=i<keyDown.Length && keyDown[i]>=0.5;
        if(i==keyDown.Length || next!=current)
        {
          if(i>start)
            runs.Add(new Run(current,start,i-start,sum/(i-start)));
          start=i;
          current=next;
          sum=0;
        }
        if(i<keyDown.Length)sum+=keyDown[i];
      }
      if(runs.Count<5 || runs.Count>1000)
        return new("",0,0,keyDown.Length,[]);
      var best=new CwHsmmResult("",0,0,keyDown.Length,[]);
      for(int wpm=minWpm;wpm<=maxWpm;wpm+=2)
      {
        double unit=1.2/wpm;
        var output=new List<CwHsmmSymbol>();
        var pattern=new System.Text.StringBuilder();
        double penalty=0;
        int decisions=0;
        void Commit(int at)
        {
          if(pattern.Length==0)return;
          if(Morse.TryGetValue(pattern.ToString(),out char ch))
            output.Add(new CwHsmmSymbol(ch,at));
          else penalty+=5;
          pattern.Clear();
        }
        foreach(Run run in runs)
        {
          double seconds=run.Length*secondsPerFrame;
          double units=seconds/unit;
          if(run.On)
          {
            double dotError=Math.Abs(units-1);
            double dashError=Math.Abs(units-3);
            bool dash=dashError<dotError;
            penalty+=Math.Pow(Math.Min(dotError,dashError)/0.65,2);
            pattern.Append(dash?'-':'.');
            decisions++;
          }
          else if(pattern.Length>0)
          {
            // An observation window may end in arbitrarily long silence
            // after the final transmitted character. It is censoring,
            // not a 40-dot Morse word gap, and must not destroy an
            // otherwise correct multi-letter transcript.
            if(run.Start+run.Length>=keyDown.Length)
            {
              Commit(run.Start);
              continue;
            }
            int distance=units>=5?7:units>=2?3:1;
            penalty+=Math.Pow((units-distance)/(distance==1?0.8:2.1),2);
            if(distance>=3)Commit(run.Start);
            if(distance==7 && output.Count>0 &&
               output[^1].Character!=' ')
              output.Add(new CwHsmmSymbol(' ',run.Start));
            decisions++;
          }
        }
        Commit(keyDown.Length-1);
        string text=new string(output.Select(x=>x.Character).ToArray()).Trim();
        if(text.Length<2 || decisions<5)continue;
        double contrast=runs.Where(x=>x.On).Select(x=>x.Mean)
          .DefaultIfEmpty(0.5).Average() -
          runs.Where(x=>!x.On).Select(x=>x.Mean)
          .DefaultIfEmpty(0.5).Average();
        double score=Math.Exp(-penalty/Math.Max(1,decisions))*
          Math.Clamp(contrast,0,1);
        if(score>best.EvidenceScore)
          best=new CwHsmmResult(text,score,wpm,keyDown.Length,output.ToArray());
      }
      return best;
    }

    /// <summary>
    /// Matched-tone short-time energy to soft keyed activity. Intentionally
    /// limited to one tracked AF frequency: this is not a blind carrier finder.
    /// </summary>
    public static float[] EstimateEnvelope(
      ReadOnlySpan<float> pcm,int sampleRate,double frequencyHz,
      int hopMilliseconds=10)
    {
      if(sampleRate<2000 || frequencyHz<=0 ||
         frequencyHz>=sampleRate/2 || hopMilliseconds is < 5 or > 50)
        throw new ArgumentOutOfRangeException(nameof(sampleRate));
      int hop=Math.Max(1,sampleRate*hopMilliseconds/1000);
      int window=2*hop;
      int count=pcm.Length<window ? 0 :
        1+(pcm.Length-window)/hop;
      if(count==0)return [];
      // One oscillator setup per frame rather than a transcendental per
      // sample (important when testing eight CW lanes on a 6-core host).
      var weights=new double[window];
      for(int n=0;n<window;n++)
        weights[n]=0.5-0.5*Math.Cos(2*Math.PI*n/(window-1));
      double step=2*Math.PI*frequencyHz/sampleRate;
      double cs=Math.Cos(step),sn=Math.Sin(step);
      var power=new double[count];
      for(int frame=0;frame<count;frame++)
      {
        int start=frame*hop;
        double angle=step*start;
        double cos=Math.Cos(angle),sin=Math.Sin(angle);
        double real=0,imag=0;
        for(int n=0;n<window;n++)
        {
          double value=pcm[start+n]*weights[n];
          real+=value*cos;
          imag+=value*sin;
          double nextCos=cos*cs-sin*sn;
          sin=sin*cs+cos*sn;
          cos=nextCos;
        }
        power[frame]=real*real+imag*imag;
      }
      double[] sorted=power.ToArray();
      Array.Sort(sorted);
      double lo=sorted[(int)(0.15*(sorted.Length-1))];
      double hi=sorted[(int)(0.85*(sorted.Length-1))];
      if(hi<=lo*1.8 || hi<1e-16)
        return Enumerable.Repeat(0.5f,count).ToArray();
      // A linear-power sigmoid maps genuine key-up silence (lo~=0) to
      // ~0.5, yielding phantom long key-down runs. Compare log power
      // instead: separate signal and noise decades without depending on
      // absolute USB AF amplitude or inventing activity in flat noise.
      double floor=Math.Max(hi*1e-6,1e-18);
      double loLog=Math.Log(Math.Max(lo,floor));
      double hiLog=Math.Log(Math.Max(hi,floor*1.01));
      double mid=0.5*(loLog+hiLog);
      double width=Math.Max(0.10,0.18*(hiLog-loLog));
      return power.Select(x=>
        (float)Math.Clamp(
          0.5+0.5*Math.Tanh((Math.Log(Math.Max(x,floor))-mid)/width),
          0,1)).ToArray();
    }

    /// <summary>Conservative A/B arbitration: neural output remains primary.</summary>
    public static DeepCwDecodedText Arbitrate(
      DeepCwDecodedText neural,CwHsmmResult alternative)
    {
      if(neural.Text.Length>0 || alternative.EvidenceScore<0.93 ||
         alternative.Text.Trim().Length<2 || neural.OutputFrameCount<=0)
        return neural;
      DeepCwDecodedSymbol[] symbols=alternative.Symbols
        .Select(x=>new DeepCwDecodedSymbol(x.Character,
          Math.Clamp((int)Math.Round(x.Frame*(neural.OutputFrameCount-1.0)/
            Math.Max(1,alternative.InputFrames-1)),0,
            neural.OutputFrameCount-1),alternative.EvidenceScore))
        .ToArray();
      return new DeepCwDecodedText(alternative.Text,symbols,
        neural.OutputFrameCount);
    }
  }
}
