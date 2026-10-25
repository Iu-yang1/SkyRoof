using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyRoof.CW
{
  /// <summary>
  /// Bounded local coherent frequency/rate calibration, operating on
  /// immutable PCM. This is deliberately separate from the track identity
  /// filter; it must never change user-visible Kalman IDs or state.
  ///
  /// Find the narrow carrier peak in overlapping Hann-windowed 2 s PCM
  /// segments. Fit the measured frequency residuals to the expected
  /// constant-slope Doppler trajectory. This avoids the catastrophic
  /// phase decorrelation of an 8 s global fit from a 0.25–0.5 Hz frequency
  /// bias while retaining a cheap O(window * grid) bounded search.
  /// </summary>
  internal static class CwCoherentFrequencyRefiner
  {
    internal readonly record struct Refinement(
      CwSignalTrack Track,
      bool Applied,
      int AcceptedWindows,
      double ResidualRmsHz);

    internal static Refinement Refine(
      ReadOnlySpan<float> pcm,
      int sampleRate,
      CwSignalTrack track,
      double nearestOtherHz)
    {
      if (sampleRate < 6000 || pcm.Length < sampleRate * 4 ||
          !double.IsFinite(track.FrequencyHz) ||
          !double.IsFinite(track.DriftHzPerSecond))
        return new(track, false, 0, double.NaN);

      double duration = pcm.Length / (double)sampleRate;
      double search = Math.Min(3.0,
        Math.Max(1.0, nearestOtherHz * 0.40));
      const double searchStep = 0.25;
      const double windowSeconds = 2.0;
      const double hopSeconds = 0.75;
      // Keep the decimated observation rate > 2x the carrier's highest
      // predicted frequency. Input remains untouched for SIC/DeepCW.
      double largestHz = track.FrequencyHz +
        Math.Abs(track.DriftHzPerSecond) * duration + search + 150;
      int stride = Math.Clamp(
        (int)(sampleRate / (2.15 * largestHz)), 1, 16);
      var observations =
        new List<(double Time, double Offset, double Weight)>();

      for (double centerSeconds = 1.0;
        centerSeconds <= duration - 1.0 + 1e-6;
        centerSeconds += hopSeconds)
      {
        int center = (int)Math.Round(centerSeconds * sampleRate);
        int half = (int)Math.Round(windowSeconds * sampleRate / 2.0);
        int start = Math.Max(0, center - half);
        int stop = Math.Min(pcm.Length - 1, center + half);
        double centerToEnd =
          (center - (pcm.Length - 1)) / (double)sampleRate;
        double baseFrequencyHz = track.FrequencyHz +
          track.DriftHzPerSecond * centerToEnd;
        int gridHalf = (int)Math.Floor(search / searchStep);
        int nGrid = 2 * gridHalf + 1;
        var powers = new double[nGrid];

        for (int g = 0; g < nGrid; g++)
        {
          double candidate = (g - gridHalf) * searchStep;
          double real = 0, imag = 0;
          for (int n = start; n <= stop; n += stride)
          {
            double local = (n - center) / (double)sampleRate;
            double hann = 0.5 + 0.5 * Math.Cos(
              Math.PI * local / (windowSeconds / 2));
            double angle = 2 * Math.PI * (
              (baseFrequencyHz + candidate) * local +
              0.5 * track.DriftHzPerSecond * local * local);
            double value = pcm[n] * hann;
            real += value * Math.Cos(angle);
            imag += value * Math.Sin(angle);
          }
          powers[g] = real * real + imag * imag;
        }

        int peak = Array.IndexOf(powers, powers.Max());
        // Do not regress clipped estimates: the missing true carrier
        // might lie outside the search range (or be another station).
        if (peak <= 0 || peak >= nGrid - 1)
          continue;
        double left=powers[peak-1], mid=powers[peak],
          right=powers[peak+1];
        double denominator = left - 2*mid + right;
        double fraction = denominator < -1e-12
          ? Math.Clamp(0.5 * (left-right)/denominator,-1,1)
          : 0;
        double observedOffset =
          (peak-gridHalf+fraction)*searchStep;

        // A near-zero matched peak is not a physical tone. Also reject
        // completely flat/ragged objective surfaces from silence/noise.
        double mean=powers.Average();
        if (mid <= 1.18 * Math.Max(mean,1e-20))
          continue;
        observations.Add((
          centerToEnd, observedOffset,
          Math.Clamp(mid / Math.Max(mean,1e-20),1,30)));
      }

      if(observations.Count < 4)
        return new(track,false,observations.Count,double.NaN);

      // Weighted linear fit delta_f(t)=a+b*t. A strong keyed Morse tone
      // offers several good segments even when others are key-up.
      double sw=0,st=0,sf=0,stt=0,stf=0;
      foreach(var item in observations)
      {
        sw+=item.Weight;
        st+=item.Weight*item.Time;
        sf+=item.Weight*item.Offset;
        stt+=item.Weight*item.Time*item.Time;
        stf+=item.Weight*item.Time*item.Offset;
      }
      double d=sw*stt-st*st;
      if(d <= 1e-8 || !double.IsFinite(d))
        return new(track,false,observations.Count,double.NaN);
      double correctionHz=(sf*stt-st*stf)/d;
      double correctionRate=(sw*stf-st*sf)/d;
      double rss=observations.Sum(item=>item.Weight *
        Math.Pow(item.Offset-correctionHz-
          correctionRate*item.Time,2));
      double rms=Math.Sqrt(rss/sw);

      // Use local observations only as a starting point. Their finite
      // 2 s time resolution can systematically bias the end-frequency
      // extrapolation by ~0.5 Hz even when the input track is exact.
      // A physically coherent whole-window likelihood MUST improve before
      // any private oscillator estimate is accepted.
      if(!double.IsFinite(correctionHz) ||
         !double.IsFinite(correctionRate) ||
         Math.Abs(correctionHz)>2.5 ||
         Math.Abs(correctionRate)>0.9 ||
         rms>1.1)
        return new(track,false,observations.Count,rms);

      double midIndex=(pcm.Length-1)/2.0;
      double midTimeToEnd=
        (midIndex-(pcm.Length-1))/sampleRate;
      double nominalMidFrequency=track.FrequencyHz+
        track.DriftHzPerSecond*midTimeToEnd;

      double CoherentScore(double deltaMidHz,double deltaRate)
      {
        double frequency=nominalMidFrequency+deltaMidHz;
        double drift=track.DriftHzPerSecond+deltaRate;
        double real=0,imag=0;
        for(int n=0;n<pcm.Length;n+=stride)
        {
          double t=(n-midIndex)/sampleRate;
          double angle=2*Math.PI*(frequency*t+
            0.5*drift*t*t);
          double sample=pcm[n];
          real+=sample*Math.Cos(angle);
          imag+=sample*Math.Sin(angle);
        }
        return real*real+imag*imag;
      }

      double priorScore=CoherentScore(0,0);
      double bestMid=0,bestRate=0,bestScore=priorScore;
      double seedMid=correctionHz+
        correctionRate*midTimeToEnd;

      // Two well-defined starting points (original Kalman prediction and
      // robust local line fit), with a short bounded coordinate search.
      // Recenter phase at the midpoint to reduce frequency/rate coupling.
      foreach(var seed in new[] { (Middle:0.0,Rate:0.0),
                                  (Middle:seedMid,Rate:correctionRate) })
      {
        double centerHz=seed.Middle,rate=seed.Rate;
        double quality=CoherentScore(centerHz,rate);
        for(int iteration=0;iteration<2;iteration++)
        {
          double fStep=iteration==0 ? 0.10 : 0.05;
          double fRange=iteration==0 ? 0.80 : 0.20;
          int fSteps=(int)Math.Round(fRange/fStep);
          double startingCenter=centerHz;
          double candidateCenter=centerHz;
          for(int k=-fSteps;k<=fSteps;k++)
          {
            double candidate=startingCenter+k*fStep;
            if(Math.Abs(candidate)>3.0)continue;
            double score=CoherentScore(candidate,rate);
            if(score>quality)
            {
              quality=score;
              candidateCenter=candidate;
            }
          }
          centerHz=candidateCenter;

          double rStep=iteration==0 ? 0.025 : 0.0125;
          double rRange=iteration==0 ? 0.20 : 0.05;
          int rSteps=(int)Math.Round(rRange/rStep);
          double startingRate=rate;
          double candidateRate=rate;
          for(int k=-rSteps;k<=rSteps;k++)
          {
            double candidate=startingRate+k*rStep;
            if(Math.Abs(candidate)>0.9)continue;
            double score=CoherentScore(centerHz,candidate);
            if(score>quality)
            {
              quality=score;
              candidateRate=candidate;
            }
          }
          rate=candidateRate;
        }
        if(quality>bestScore)
        {
          bestScore=quality;
          bestMid=centerHz;
          bestRate=rate;
        }
      }

      double finalEndHz=bestMid-bestRate*midTimeToEnd;
      if(bestScore < 1.25*Math.Max(priorScore,1e-15) ||
         !double.IsFinite(finalEndHz) ||
         Math.Abs(finalEndHz)>2.25 ||
         Math.Abs(bestRate)>0.8)
        return new(track,false,observations.Count,rms);

      return new(track with {
        FrequencyHz=track.FrequencyHz+finalEndHz,
        DriftHzPerSecond=track.DriftHzPerSecond+bestRate
      },true,observations.Count,rms);
    }
  }
}
