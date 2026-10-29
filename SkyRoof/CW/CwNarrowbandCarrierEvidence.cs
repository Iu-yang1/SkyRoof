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
      double DutyCycle, int OnRuns, int HighContrastFrames,
      double PhaseCoherence = 0);

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
      // Keep the temporal sample cadence close to the existing 15ms
      // fast ridge path. A 40ms hop aliases legitimate, independently
      // keyed 15Hz doublets into too many transitions per observed frame
      // and makes the morphology gate reject real CW as if it were noise.
      int hop=Math.Max(24,(int)Math.Round(0.020*effectiveRate));
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
      // Coherent demodulated phasors are required in addition to spectral
      // contrast. A colored noise hump may create a >10dB peak-over-floor
      // and random keying-shaped envelopes without a stable oscillator.
      var phaseReal=new double[frameCount];
      var phaseImag=new double[frameCount];
      var phaseMagnitude=new double[frameCount];
      var phaseTime=new double[frameCount];
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
        // The per-frame matched oscillator starts at local phase zero,
        // whereas a real CW oscillator is continuous in the PCM sample
        // timeline. Compensate its predicted carrier + Doppler phase.
        double timeFromEnd=(start-pcm.Length)/(double)rate;
        double carrierPhase=2*Math.PI*(
          track.FrequencyHz*timeFromEnd +
          0.5*track.DriftHzPerSecond*timeFromEnd*timeFromEnd);
        double cp=Math.Cos(carrierPhase),sp=Math.Sin(carrierPhase);
        phaseReal[frame]=real[0]*cp-imag[0]*sp;
        phaseImag[frame]=real[0]*sp+imag[0]*cp;
        phaseMagnitude[frame]=Math.Sqrt(signal);
        phaseTime[frame]=timeFromEnd;
        if(contrast>=4.0)high++;
        maxContrast=Math.Max(maxContrast,contrast);
      }

      CwKeyingAssessment morphology=CwKeyingGate.AssessSnrSeries(
        contrasts,hop/effectiveRate,minimumKeyingDepthDb:5.5);
      // Requiring sustained contrast in a complete PCM timeline protects
      // against isolated large noise periodograms. The 4 dB per-frame gate
      // is deliberately below a 6 dB fixed threshold to preserve weaker
      // CW. The higher duty requirement (22%) controls false alarms.
      double coherentPersistence=EstimatePhaseCoherence(
        phaseReal,phaseImag,phaseMagnitude,phaseTime);
      bool credible=morphology.IsKeyed &&
        high>=Math.Max(5,(int)Math.Ceiling(0.22*frameCount)) &&
        morphology.OnRuns>=2 &&
        coherentPersistence>=0.60;
      return new Result(credible,maxContrast,
        morphology.KeyingDepthDb,morphology.DutyCycle,
        morphology.OnRuns,high,coherentPersistence);
    }

    // A keyed oscillator has consistent phase across independent key-down
    // segments; Gaussian band-limited noise does not, even if its spectrum
    // is 10–20 dB above a nearby AF floor. Do not demand exact tracker
    // center frequency: 240ms STFT gives a nonzero frequency uncertainty.
    // Search a small +/-3 Hz residual correction at 0.25 Hz resolution
    // before rejecting a weak or Doppler-drifting CW signal.
    private static double EstimatePhaseCoherence(
      double[] real,double[] imag,double[] magnitude,double[] seconds)
    {
      if(real.Length<14 || imag.Length!=real.Length ||
         magnitude.Length!=real.Length || seconds.Length!=real.Length)
        return 0;
      double[] ordered=(double[])magnitude.Clone();
      Array.Sort(ordered);
      double cutoff=ordered[(int)Math.Floor(0.55*(ordered.Length-1))];
      double totalMagnitude=0;
      int selected=0;
      for(int i=0;i<magnitude.Length;i++)
        if(magnitude[i]>cutoff){totalMagnitude+=magnitude[i];selected++;}
      if(selected<8 || totalMagnitude<1e-9)return 0;

      double best=0;
      for(int trial=-12;trial<=12;trial++)
      {
        double correctionHz=trial*0.25;
        double sumRe=0,sumIm=0;
        for(int i=0;i<magnitude.Length;i++)
        {
          if(magnitude[i]<=cutoff)continue;
          double phase=2*Math.PI*correctionHz*seconds[i];
          double cp=Math.Cos(phase),sp=Math.Sin(phase);
          sumRe+=real[i]*cp-imag[i]*sp;
          sumIm+=real[i]*sp+imag[i]*cp;
        }
        double coherence=Math.Sqrt(sumRe*sumRe+sumIm*sumIm)/
          totalMagnitude;
        best=Math.Max(best,coherence);
      }
      return Math.Clamp(best,0,1);
    }
  }
}
