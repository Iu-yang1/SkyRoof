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

      // A consistent trajectory is needed before phase-coherent
      // cancellation; otherwise retain the unmodified original estimate.
      if(!double.IsFinite(correctionHz) ||
         !double.IsFinite(correctionRate) ||
         Math.Abs(correctionHz)>2.25 ||
         Math.Abs(correctionRate)>0.8 ||
         rms>0.85)
        return new(track,false,observations.Count,rms);

      return new(track with {
        FrequencyHz=track.FrequencyHz+correctionHz,
        DriftHzPerSecond=track.DriftHzPerSecond+correctionRate
      },true,observations.Count,rms);
    }
  }
}
