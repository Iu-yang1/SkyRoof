using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  /// <summary>
  /// Experimental time-domain multi-carrier CW separator for overlapping
  /// 5–25 Hz Pileup. Regress measured PCM against the actual predicted
  /// carrier phases (cos/sin) in short Hann windows, rather than dividing
  /// magnitude-bin energy between nearly coincident Gaussian masks.
  ///
  /// This is a bounded, regularized 2–3-carrier least-squares estimator,
  /// NOT successive waveform subtraction and NOT a guaranteed 5 Hz resolver.
  /// Retain it as an opt-in until real-model CER and live recordings validate.
  /// </summary>
  internal static class CwCoherentDenseSeparator
  {
    internal const double MaximumPairSpacingHz = 27.0;
    private const int MaximumSources = 3;

    internal static bool HasNearNeighbor(CwSignalTrack target,
      IReadOnlyList<CwSignalTrack> allTracks) =>
      allTracks.Any(x => x.Id != target.Id && x.Confirmed &&
        Math.Abs(x.FrequencyHz-target.FrequencyHz) <= MaximumPairSpacingHz &&
        Math.Abs(x.FrequencyHz-target.FrequencyHz) >= 2.0);

    internal static DeepCwTensor BuildLaneTensor(
      ReadOnlySpan<float> input,
      int sourceSampleRate,
      DeepCwModelMetadata metadata,
      CwSignalTrack target,
      IReadOnlyList<CwSignalTrack> allTracks,
      double targetCenterHz,
      DeepCwWidebandFeatureWindow? originalFeatures = null,
      IReadOnlyDictionary<int,float[]>? originalActivities = null,
      bool cancelStrongerInterference = false)
    {
      if (sourceSampleRate < 6000 ||
          metadata.SampleRate < 1000 ||
          input.Length < sourceSampleRate / 2)
        throw new ArgumentException("Coherent CW fitting needs at least 0.5 s of suitable PCM.");
      if (targetCenterHz <= metadata.MinFrequencyHz + 40 ||
          targetCenterHz >= metadata.MaxFrequencyHz - 40)
        throw new ArgumentOutOfRangeException(nameof(targetCenterHz));

      // Do not spend polynomial fit capacity on remote, non-overlapping
      // signals; all bases are independently phase-driven by tracker state.
      CwSignalTrack[] sources = new[] { target }
        .Concat(allTracks.Where(t => t.Id != target.Id && t.Confirmed &&
          Math.Abs(t.FrequencyHz-target.FrequencyHz) <= MaximumPairSpacingHz)
          .OrderBy(t=>Math.Abs(t.FrequencyHz-target.FrequencyHz))
          .Take(MaximumSources-1))
        .ToArray();

      int outputSamples = checked((int)Math.Round(
        (double)input.Length * metadata.SampleRate/sourceSampleRate));
      int modelHop = metadata.HopLength;
      int frameCount = 1 + outputSamples/modelHop;
      int stride = sourceSampleRate >= 32000 ? 4 : 1;
      int sampledCount = 1 + (input.Length-1)/stride;
      var basis = new double[2*sources.Length][];
      for (int q=0; q<basis.Length; q++)
        basis[q]=new double[sampledCount];

      // Absolute phase offset is free in each fitted in-phase/quadrature
      // pair. A single precomputed basis grid amortizes trig across frames.
      for (int k=0;k<sampledCount;k++)
      {
        int n=k*stride;
        double t=(n-(input.Length-1))/(double)sourceSampleRate;
        for (int j=0;j<sources.Length;j++)
        {
          double phase=2*Math.PI*(sources[j].FrequencyHz*t+
            0.5*sources[j].DriftHzPerSecond*t*t);
          basis[2*j][k]=Math.Cos(phase);
          basis[2*j+1][k]=Math.Sin(phase);
        }
      }

      // Estimate a stable absolute phase for every oscillator over the
      // full contiguous window. CW envelopes change, but a keyed carrier's
      // phase is approximately coherent while the oscillator and Doppler
      // prediction remain correct. Globally estimated phase removes one
      // quadrature nuisance parameter per source in each short frame.
      int quadratures=basis.Length;
      var phaseGram=new double[quadratures,quadratures];
      var phaseRhs=new double[quadratures];
      var phaseSolution=new double[quadratures];
      for(int k=0;k<sampledCount;k++)
      {
        double sample=input[k*stride];
        for(int i=0;i<quadratures;i++)
        {
          phaseRhs[i]+=basis[i][k]*sample;
          for(int j=0;j<=i;j++)
            phaseGram[i,j]+=basis[i][k]*basis[j][k];
        }
      }
      for(int i=0;i<quadratures;i++)
      {
        for(int j=0;j<i;j++)phaseGram[j,i]=phaseGram[i,j];
        phaseGram[i,i]+=sampledCount*0.0003;
      }
      if(!SolvePositiveDefinite(
          phaseGram,phaseRhs,phaseSolution))
        throw new InvalidOperationException(
          "CW phase-locked fit was not positive definite.");

      var locked=new double[sources.Length][];
      for(int j=0;j<sources.Length;j++)
      {
        double cosine=phaseSolution[2*j];
        double sine=phaseSolution[2*j+1];
        double scale=Math.Sqrt(cosine*cosine+sine*sine);
        if(scale<1e-9)scale=1.0;
        cosine/=scale;
        sine/=scale;
        locked[j]=new double[sampledCount];
        for(int k=0;k<sampledCount;k++)
          locked[j][k]=cosine*basis[2*j][k]+
            sine*basis[2*j+1][k];
      }

      double nearest=sources.Skip(1)
        .Select(x=>Math.Abs(x.FrequencyHz-target.FrequencyHz))
        .DefaultIfEmpty(double.PositiveInfinity).Min();
      double windowSeconds=nearest <= 9 ? 0.160 :
        nearest <= 17 ? 0.100 : 0.080;
      int halfWindowSamples = Math.Max(16,
        (int)Math.Round(windowSeconds*sourceSampleRate/(2*stride)));
      int size=locked.Length;
      var amplitudes=new double[frameCount];
      var otherAmplitudes=new double[sources.Length][];
      for(int j=0;j<sources.Length;j++)
        otherAmplitudes[j]=new double[frameCount];
      var gram=new double[size,size];
      var rhs=new double[size];
      var solution=new double[size];
      var augmented=new double[size,size];

      for(int frame=0;frame<frameCount;frame++)
      {
        long center = (long)Math.Round(
          (double)frame*modelHop*sourceSampleRate/metadata.SampleRate/stride);
        Array.Clear(gram);
        Array.Clear(rhs);
        double totalWeight=0;
        long start=Math.Max(0,center-halfWindowSamples);
        long stop=Math.Min(sampledCount-1,center+halfWindowSamples);
        for(long p=start;p<=stop;p++)
        {
          double offset=(p-center)/(double)halfWindowSamples;
          double weight=0.5+0.5*Math.Cos(Math.PI*offset);
          if(weight<=0)continue;
          int k=(int)p;
          double sample=input[k*stride];
          totalWeight+=weight;
          for(int i=0;i<size;i++)
          {
            double wi=weight*locked[i][k];
            rhs[i]+=wi*sample;
            for(int j=0;j<=i;j++)
              gram[i,j]+=wi*locked[j][k];
          }
        }
        for(int i=0;i<size;i++)
          for(int j=0;j<i;j++)
            gram[j,i]=gram[i,j];

        // Tikhonov regularization guards the inherently ill-conditioned
        // 5 Hz fit and keeps AWGN from producing arbitrarily large gains.
        // Express lambda relative to each column's expected tone energy.
        double regularizer=(nearest<=9 ? 0.012 : 0.002)*
          totalWeight/2.0;
        for(int i=0;i<size;i++)
        {
          for(int j=0;j<size;j++)
            augmented[i,j]=gram[i,j];
          augmented[i,i]+=regularizer;
        }
        if(!SolvePositiveDefinite(augmented,rhs,solution))
        {
          amplitudes[frame]=0;
          continue;
        }
        // A phase-locked carrier has one signed physical amplitude; do
        // not rectifiy negative noise into a fictitious CW key-down.
        amplitudes[frame]=Math.Clamp(solution[0],0,2.0);
        for(int j=0;j<sources.Length;j++)
          otherAmplitudes[j][frame]=Math.Clamp(solution[j],0,2.0);
      }

      if(cancelStrongerInterference && sources.Length >= 2)
      {
        // Physically subtract the modelled STRONGER carrier from the
        // original waveform, retaining the target, RF noise and actual
        // spectrum statistics. Only called on weaker near-far lanes.
        float[] residual=input.ToArray();
        for(int j=1;j<sources.Length;j++)
        {
          if(sources[j].SnrDb < target.SnrDb+6)
            continue;
          double cosine=phaseSolution[2*j];
          double sine=phaseSolution[2*j+1];
          double scale=Math.Sqrt(cosine*cosine+sine*sine);
          if(scale<1e-10)continue;
          cosine/=scale; sine/=scale;
          for(int n=0;n<residual.Length;n++)
          {
            double hopPosition=n/(double)sourceSampleRate*
              metadata.SampleRate/modelHop;
            int lo=Math.Min((int)hopPosition,frameCount-1);
            int hi=Math.Min(lo+1,frameCount-1);
            double frac=hopPosition-lo;
            double amplitude=otherAmplitudes[j][lo]*(1-frac)+
              otherAmplitudes[j][hi]*frac;
            double tau=(n-(input.Length-1))/(double)sourceSampleRate;
            double phi=2*Math.PI*(sources[j].FrequencyHz*tau+
              0.5*sources[j].DriftHzPerSecond*tau*tau);
            double wave=amplitude*(cosine*Math.Cos(phi)+sine*Math.Sin(phi));
            residual[n]-=(float)(0.95*wave);
          }
        }
        var corrected=DeepCwWidebandFeatureWindow.Create(
          residual,sourceSampleRate,metadata);
        var onlyTarget=new[]{target};
        var correctedActivity=corrected.EstimateActivities(
          onlyTarget,new CwCarrierActivityEstimator());
        return corrected.BuildLaneTensor(target,
          onlyTarget,correctedActivity,targetCenterHz);
      }

      if(originalFeatures != null && originalActivities != null)
      {
        if(originalFeatures.FrameCount != frameCount)
          throw new ArgumentException(
            "CW coherent activity does not match the model frame axis.");

        var activity=new Dictionary<int,float[]>(originalActivities);
        for(int source=0;source<sources.Length;source++)
        {
          CwSignalTrack station=sources[source];
          if(!originalActivities.TryGetValue(station.Id,
              out float[]? original) ||
             original.Length != frameCount)
            continue;
          double[] values=otherAmplitudes[source].ToArray();
          Array.Sort(values);
          double lo=values[(int)(0.15*(values.Length-1))];
          double hi=values[(int)(0.85*(values.Length-1))];
          double span=hi-lo;
          if(span < 1e-5)continue;
          var blended=new float[frameCount];
          for(int f=0;f<frameCount;f++)
          {
            double fitted=Math.Clamp(
              (otherAmplitudes[source][f]-lo)/(span*0.82),0,1);
            // Keep original HMM evidence: coherent fit is an additional
            // physical cue, not an unverified hard ON/OFF gate.
            blended[f]=(float)(0.55*fitted+
              0.45*Math.Clamp(original[f],0,1));
          }
          activity[station.Id]=blended;
        }
        return originalFeatures.BuildLaneTensor(
          target,allTracks,activity,targetCenterHz);
      }

      // Construct a single model-centred CW waveform from the recovered
      // keyed envelope, retaining the familiar STFT frontend and input
      // normalization of the pinned DeepCW model.
      var reconstructed = new float[outputSamples];
      for(int i=0;i<outputSamples;i++)
      {
        double position=i/(double)modelHop;
        int lo=Math.Min((int)position,frameCount-1);
        int hi=Math.Min(lo+1,frameCount-1);
        double frac=position-lo;
        double amplitude=amplitudes[lo]*(1-frac)+amplitudes[hi]*frac;
        reconstructed[i]=(float)(amplitude*
          Math.Cos(2*Math.PI*targetCenterHz*i/metadata.SampleRate));
      }
      return DeepCwFeatureWindow.Create(
        reconstructed,metadata.SampleRate,metadata)
        .BuildStandardTensor();
    }

    // In-place Cholesky on a small regularized normal matrix (size <= 6).
    private static bool SolvePositiveDefinite(
      double[,] matrix,double[] rhs,double[] result)
    {
      int size=rhs.Length;
      var y=new double[size];
      for(int i=0;i<size;i++)
      {
        for(int j=0;j<=i;j++)
        {
          double sum=matrix[i,j];
          for(int k=0;k<j;k++)
            sum-=matrix[i,k]*matrix[j,k];
          if(i==j)
          {
            if(!double.IsFinite(sum)||sum<=1e-12)return false;
            matrix[i,j]=Math.Sqrt(sum);
          }
          else matrix[i,j]=sum/matrix[j,j];
        }
        double v=rhs[i];
        for(int k=0;k<i;k++)v-=matrix[i,k]*y[k];
        y[i]=v/matrix[i,i];
      }
      for(int i=size-1;i>=0;i--)
      {
        double v=y[i];
        for(int k=i+1;k<size;k++)
          v-=matrix[k,i]*result[k];
        result[i]=v/matrix[i,i];
      }
      return result.All(double.IsFinite);
    }
  }
}
