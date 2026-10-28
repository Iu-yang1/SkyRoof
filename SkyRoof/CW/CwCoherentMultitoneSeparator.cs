namespace SkyRoof.CW
{
  /// <summary>
  /// Experimental bounded coherent real-I/Q least-squares demixer.
  /// Fits real cosine/sine coefficients jointly, per short local block.
  /// It is NOT a verified solution for arbitrary keyed 5 Hz pileups.
  /// No in-place audio changes, and failed fits fall back to the legacy path.
  /// </summary>
  public static class CwCoherentMultitoneSeparator
  {
    public static bool TrySeparate(
      ReadOnlySpan<float> pcm, int sampleRate,
      IReadOnlyList<CwSignalTrack> tracks,
      out IReadOnlyDictionary<int,float[]> separated)
    {
      separated=new Dictionary<int,float[]>();
      if(sampleRate<2000 || pcm.Length<sampleRate/4 ||
         tracks.Count<2 || tracks.Count>4 ||
         tracks.Select(t=>t.Id).Distinct().Count()!=tracks.Count ||
         tracks.Any(t=>!double.IsFinite(t.FrequencyHz) ||
           t.FrequencyHz<80 || t.FrequencyHz>=sampleRate*0.48))
        return false;
      double[] f=tracks.Select(t=>t.FrequencyHz).ToArray();
      double spacing=f.Order().Zip(f.Order().Skip(1),(a,b)=>b-a).Min();
      if(spacing<3 || !double.IsFinite(spacing))return false;
      // 0.25..1 second compromises CW keying fidelity vs close-tone
      // matrix conditioning. Under severe overlap the residual rejects fits.
      int block=Math.Clamp(
        (int)Math.Round(sampleRate*Math.Clamp(3.0/spacing,0.25,1)),
        sampleRate/4,sampleRate);
      int sampleCount=pcm.Length; // do not capture a ReadOnlySpan in a lambda
      var output=tracks.ToDictionary(t=>t.Id,_=>new float[sampleCount]);
      int dimensions=tracks.Count*2;
      const double twoPi=2*Math.PI;
      double pcmDuration=pcm.Length/(double)sampleRate;
      double totalInput=0,totalResidual=0;
      for(int start=0;start<pcm.Length;start+=block)
      {
        int len=Math.Min(block,pcm.Length-start);
        if(len<sampleRate/8)return false;
        var normal=new double[dimensions,dimensions];
        var target=new double[dimensions];
        var basis=new double[dimensions];
        double inputEnergy=0;
        for(int n=0;n<len;n++)
        {
          double value=pcm[start+n];
          double time=(start+n)/(double)sampleRate;
          double weight=0.5-0.5*Math.Cos(twoPi*(n+0.5)/len);
          for(int k=0;k<tracks.Count;k++)
          {
            double sinceEnd=time-pcmDuration;
            double phase=twoPi*(f[k]*time+
              0.5*tracks[k].DriftHzPerSecond*sinceEnd*sinceEnd);
            basis[2*k]=Math.Cos(phase);
            basis[2*k+1]=Math.Sin(phase);
          }
          for(int i=0;i<dimensions;i++)
          {
            target[i]+=weight*basis[i]*value;
            for(int j=0;j<=i;j++)
              normal[i,j]+=weight*basis[i]*basis[j];
          }
          inputEnergy+=value*value;
        }
        for(int i=0;i<dimensions;i++)
        {
          for(int j=0;j<i;j++)normal[j,i]=normal[i,j];
          normal[i,i]+=Math.Max(1e-9,normal[i,i]*0.0001);
        }
        if(!TrySolve(normal,target,out double[] coeff))return false;
        double residual=0;
        for(int n=0;n<len;n++)
        {
          double time=(start+n)/(double)sampleRate;
          double combined=0;
          for(int k=0;k<tracks.Count;k++)
          {
            double sinceEnd=time-pcmDuration;
            double phase=twoPi*(f[k]*time+
              0.5*tracks[k].DriftHzPerSecond*sinceEnd*sinceEnd);
            double y=coeff[2*k]*Math.Cos(phase)+
              coeff[2*k+1]*Math.Sin(phase);
            output[tracks[k].Id][start+n]=(float)y;
            combined+=y;
          }
          double error=pcm[start+n]-combined;
          residual+=error*error;
        }
        totalInput+=inputEnergy;
        totalResidual+=residual;
      }
      if(totalInput<1e-12 || totalResidual>totalInput*0.9)
        return false;
      separated=output;
      return true;
    }

    private static bool TrySolve(double[,] a,double[] b,out double[] x)
    {
      int n=b.Length;
      x=new double[n];
      var m=(double[,])a.Clone();
      var rhs=(double[])b.Clone();
      for(int col=0;col<n;col++)
      {
        int pivot=col;
        for(int r=col+1;r<n;r++)
          if(Math.Abs(m[r,col])>Math.Abs(m[pivot,col]))pivot=r;
        if(Math.Abs(m[pivot,col])<1e-9)return false;
        if(pivot!=col)
        {
          for(int c=0;c<n;c++)
            (m[col,c],m[pivot,c])=(m[pivot,c],m[col,c]);
          (rhs[col],rhs[pivot])=(rhs[pivot],rhs[col]);
        }
        double divisor=m[col,col];
        for(int c=col;c<n;c++)m[col,c]/=divisor;
        rhs[col]/=divisor;
        for(int r=0;r<n;r++)
        {
          if(r==col)continue;
          double factor=m[r,col];
          for(int c=col;c<n;c++)m[r,c]-=factor*m[col,c];
          rhs[r]-=factor*rhs[col];
        }
      }
      for(int i=0;i<n;i++)
      {
        if(!double.IsFinite(rhs[i]))return false;
        x[i]=rhs[i];
      }
      return true;
    }
  }
}
