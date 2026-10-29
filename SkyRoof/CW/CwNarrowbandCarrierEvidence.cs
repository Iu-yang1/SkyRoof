namespace SkyRoof.CW
{
  /// <summary>
  /// Independent physical cross-check for sparse STFT peak-based CW
  /// detection. A transient peak can create on/off holes in the scanner's
  /// observation list even when there is no keyed transmitter. This guard
  /// measures the complete PCM on *every* time frame, including intervals
  /// without detected peaks, and compares a matched CW tone to four local
  /// off-frequency controls. The original spectral tracker remains wide and
  /// sensitive; only verification as keyed CW is gated.
  /// </summary>
  public static class CwNarrowbandCarrierEvidence
  {
    public readonly record struct Result(
      bool IsKeyed, double PeakContrastDb, double KeyingDepthDb,
      double DutyCycle, int OnRuns, int HighContrastFrames);

    public static Result Assess(CwAudioSnapshot snapshot, CwSignalTrack track)
    {
      float[] pcm=snapshot.Samples;
      int rate=snapshot.SampleRate;
      if(rate<2000 || pcm.Length<rate/2 ||
         !double.IsFinite(track.FrequencyHz) ||
         track.FrequencyHz<90 || track.FrequencyHz>=rate*0.40)
        return default;

      int decimation=Math.Max(1,rate/12000);
      double effectiveRate=rate/(double)decimation;
      if(track.FrequencyHz+145>=0.46*effectiveRate)
        return default;

      int window=Math.Max(48,(int)Math.Round(0.080*effectiveRate));
      int hop=Math.Max(24,(int)Math.Round(0.040*effectiveRate));
      int maxOriginal=Math.Min(pcm.Length,(int)(2.8*rate));
      int firstOriginal=pcm.Length-maxOriginal;
      int available=maxOriginal/decimation;
      int frameCount=available<window ? 0 :
        1+(available-window)/hop;
      if(frameCount<14)return default;

      var hann=new double[window];
      for(int j=0;j<window;j++)
        hann[j]=0.5-0.5*Math.Cos(2*Math.PI*j/(window-1));
      double[] contrasts=new double[frameCount];
      int high=0;
      double maxContrast=double.NegativeInfinity;

      for(int frame=0;frame<frameCount;frame++)
      {
        int start=firstOriginal+frame*hop*decimation;
        double secondsFromEnd=(start+
          (window*decimation)/2.0-pcm.Length)/rate;
        double center=track.FrequencyHz+
          track.DriftHzPerSecond*secondsFromEnd;
        // Guard channels are far enough from a 5/15 Hz pileup doublet
        // not to reject independent CW, but track the AF noise shaping.
        Span<double> frequencies=stackalloc double[5]
          {center,center-140,center-78,center+78,center+140};
        Span<double> real=stackalloc double[5];
        Span<double> imag=stackalloc double[5];
        Span<double> c=stackalloc double[5];
        Span<double> si=stackalloc double[5];
        Span<double> stepC=stackalloc double[5];
        Span<double> stepS=stackalloc double[5];
        for(int k=0;k<5;k++)
        {
          double theta=2*Math.PI*frequencies[k]/effectiveRate;
          c[k]=1.0;
          si[k]=0.0;
          stepC[k]=Math.Cos(theta);
          stepS[k]=Math.Sin(theta);
        }
        for(int n=0;n<window;n++)
        {
          int source=start+n*decimation;
          double sample=0;
          for(int j=0;j<decimation;j++)
            sample+=pcm[source+j];
          sample=sample*hann[n]/decimation;
          for(int k=0;k<5;k++)
          {
            real[k]+=sample*c[k];
            imag[k]+=sample*si[k];
            double nextC=c[k]*stepC[k]-si[k]*stepS[k];
            si[k]=si[k]*stepC[k]+c[k]*stepS[k];
            c[k]=nextC;
          }
        }
        // The higher of the two flanks is deliberately used. A broad
        // AF-passband shoulder near 200–400 Hz can otherwise look >10 dB
        // above a median mixing quiet low-AF bins with noisy high-AF bins.
        double left=0.5*(
          real[1]*real[1]+imag[1]*imag[1]+
          real[2]*real[2]+imag[2]*imag[2]);
        double right=0.5*(
          real[3]*real[3]+imag[3]*imag[3]+
          real[4]*real[4]+imag[4]*imag[4]);
        double reference=Math.Max(1e-17,Math.Max(left,right));
        double signal=Math.Max(1e-17,real[0]*real[0]+imag[0]*imag[0]);
        double contrast=10*Math.Log10(signal/reference);
        contrasts[frame]=contrast;
        if(contrast>=6)high++;
        maxContrast=Math.Max(maxContrast,contrast);
      }

      CwKeyingAssessment morphology=CwKeyingGate.AssessSnrSeries(
        contrasts,hop/effectiveRate,minimumKeyingDepthDb:5.5);
      // Requiring a substantial number of independent high-contrast
      // observations protects against occasional large noise periodograms.
      bool credible=morphology.IsKeyed &&
        high>=Math.Max(5,(int)Math.Ceiling(0.17*frameCount)) &&
        morphology.OnRuns>=2;
      return new Result(credible,maxContrast,
        morphology.KeyingDepthDb,morphology.DutyCycle,
        morphology.OnRuns,high);
    }
  }
}
