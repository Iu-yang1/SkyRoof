using System.Numerics;

namespace SkyRoof.CW
{
  public readonly record struct CwLaneSignal(
    CwSignalTrack Track,
    int SampleRate,
    double TargetCenterHz,
    float[] Audio,
    float[] Envelope);

  /// <summary>
  /// Extracts one tracked CW ridge directly from the original wideband PCM.
  /// Mixing follows the track's [f, df/dt] state, a windowed-sinc low-pass
  /// rejects adjacent spectrum before decimation, and the isolated complex
  /// baseband is remodulated to the DeepCW tone center.
  ///
  /// This deliberately performs lane-specific DDC before reducing the sample
  /// rate; therefore a 2.5 kHz CW tone in 48 kHz PCM is not destroyed by a
  /// premature 3.2 kHz anti-alias filter.
  /// </summary>
  public sealed class CwLaneExtractor
  {
    public int HalfTaps { get; }
    public double PassbandHz { get; }

    public CwLaneExtractor(
      double passbandHz = 110,
      int halfTaps = 48)
    {
      if (!double.IsFinite(passbandHz) || passbandHz < 40 || passbandHz > 500)
        throw new ArgumentOutOfRangeException(nameof(passbandHz));
      if (halfTaps < 16 || halfTaps > 128)
        throw new ArgumentOutOfRangeException(nameof(halfTaps));
      PassbandHz = passbandHz;
      HalfTaps = halfTaps;
    }

    public CwLaneSignal Extract(
      ReadOnlySpan<float> input,
      int sourceSampleRate,
      CwSignalTrack track,
      int outputSampleRate,
      double targetCenterHz = 800)
    {
      if (sourceSampleRate < 1000 || outputSampleRate < 1000)
        throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
      if (input.Length < 2)
        throw new ArgumentException("CW lane input is too short.", nameof(input));
      if (!double.IsFinite(track.FrequencyHz) ||
          track.FrequencyHz <= PassbandHz ||
          track.FrequencyHz >= sourceSampleRate / 2.0 - PassbandHz)
        throw new ArgumentOutOfRangeException(nameof(track));
      if (!double.IsFinite(targetCenterHz) ||
          targetCenterHz <= PassbandHz ||
          targetCenterHz >= outputSampleRate / 2.0 - PassbandHz)
        throw new ArgumentOutOfRangeException(nameof(targetCenterHz));

      int outputLength = Math.Max(1,
        (int)Math.Round(input.Length *
          outputSampleRate / (double)sourceSampleRate));
      float[] audio = new float[outputLength];
      float[] envelope = new float[outputLength];
      double ratio = sourceSampleRate / (double)outputSampleRate;
      double normalizedCutoff = PassbandHz / sourceSampleRate;

      for (int m = 0; m < outputLength; m++)
      {
        double sourceCenter = (m + 0.5) * ratio - 0.5;
        int nearest = (int)Math.Floor(sourceCenter);
        Complex sum = Complex.Zero;
        double weightSum = 0;

        for (int tap = -HalfTaps + 1; tap <= HalfTaps; tap++)
        {
          int n = nearest + tap;
          if ((uint)n >= (uint)input.Length) continue;

          double x = n - sourceCenter;
          double sincArg = 2 * normalizedCutoff * x;
          double sinc = Math.Abs(sincArg) < 1e-12
            ? 1
            : Math.Sin(Math.PI * sincArg) / (Math.PI * sincArg);
          double windowPosition = x / HalfTaps;
          if (Math.Abs(windowPosition) >= 1) continue;

          double window = 0.5 + 0.5 *
            Math.Cos(Math.PI * windowPosition);
          double weight = 2 * normalizedCutoff * sinc * window;

          // State is defined at the end of the receive window.
          double tau = (n - (input.Length - 1)) /
            (double)sourceSampleRate;
          double phase = 2 * Math.PI *
            (track.FrequencyHz * tau +
             0.5 * track.DriftHzPerSecond * tau * tau);
          Complex mixer = new(
            Math.Cos(-phase), Math.Sin(-phase));

          sum += input[n] * weight * mixer;
          weightSum += weight;
        }

        Complex baseband = weightSum == 0
          ? Complex.Zero
          : sum / weightSum;
        envelope[m] = (float)baseband.Magnitude;

        double t = m / (double)outputSampleRate;
        double remodPhase = 2 * Math.PI * targetCenterHz * t;
        Complex carrier = new(
          Math.Cos(remodPhase), Math.Sin(remodPhase));
        audio[m] = (float)(2 * (baseband * carrier).Real);
      }

      return new(
        track,
        outputSampleRate,
        targetCenterHz,
        audio,
        envelope);
    }
  }

  /// <summary>
  /// Estimates P(key-down) from the isolated lane envelope. The probability is
  /// independent from tracker Active/Confirmed state: a confirmed station may
  /// legitimately be between Morse elements while its carrier track coasts.
  /// </summary>
  public sealed class CwCarrierActivityEstimator
  {
    public double AttackMs { get; }
    public double ReleaseMs { get; }
    public double LowQuantile { get; }
    public double HighQuantile { get; }

    public CwCarrierActivityEstimator(
      double attackMs = 8,
      double releaseMs = 24,
      double lowQuantile = 0.15,
      double highQuantile = 0.85)
    {
      if (attackMs <= 0 || releaseMs <= 0)
        throw new ArgumentOutOfRangeException(nameof(attackMs));
      if (lowQuantile < 0 || highQuantile > 1 ||
          lowQuantile >= highQuantile)
        throw new ArgumentOutOfRangeException(nameof(lowQuantile));
      AttackMs = attackMs;
      ReleaseMs = releaseMs;
      LowQuantile = lowQuantile;
      HighQuantile = highQuantile;
    }

    public float[] EstimateSampleProbabilities(
      ReadOnlySpan<float> envelope,
      int sampleRate)
    {
      if (sampleRate < 1000)
        throw new ArgumentOutOfRangeException(nameof(sampleRate));
      if (envelope.Length == 0) return [];

      double[] sorted = envelope
        .ToArray()
        .Select(x => (double)x * x)
        .OrderBy(x => x)
        .ToArray();
      double low = QuantileSorted(sorted, LowQuantile);
      double high = QuantileSorted(sorted, HighQuantile);
      double dynamic = Math.Max(high - low, 1e-12);

      double attackAlpha = 1 - Math.Exp(
        -1.0 / (sampleRate * AttackMs / 1000.0));
      double releaseAlpha = 1 - Math.Exp(
        -1.0 / (sampleRate * ReleaseMs / 1000.0));

      float[] result = new float[envelope.Length];
      double probability = 0;
      for (int i = 0; i < envelope.Length; i++)
      {
        double power = envelope[i] * envelope[i];
        double normalized = Math.Clamp((power - low) / dynamic, 0, 1);

        // Smoothstep gives a continuous observation likelihood while keeping
        // clear key-up/down plateaus. Hysteretic attack/release prevents the
        // 15 ms DeepCW hop from turning every envelope ripple into a key edge.
        double observation =
          normalized * normalized * (3 - 2 * normalized);
        double alpha = observation > probability
          ? attackAlpha
          : releaseAlpha;
        probability += alpha * (observation - probability);
        result[i] = (float)Math.Clamp(probability, 0, 1);
      }

      return result;
    }

    public float[] ToFrameProbabilities(
      ReadOnlySpan<float> sampleProbabilities,
      int sampleRate,
      int fftLength,
      int hopLength)
    {
      int pad = fftLength / 2;
      int frameCount = 1 +
        (sampleProbabilities.Length + 2 * pad - fftLength) / hopLength;
      if (frameCount <= 0) return [];

      float[] frames = new float[frameCount];
      for (int frame = 0; frame < frameCount; frame++)
      {
        int center = frame * hopLength;
        int start = Math.Max(0, center - hopLength / 2);
        int stop = Math.Min(
          sampleProbabilities.Length,
          center + hopLength / 2 + 1);
        if (stop <= start)
        {
          frames[frame] = 0;
          continue;
        }

        double sum = 0;
        for (int i = start; i < stop; i++)
          sum += sampleProbabilities[i];
        frames[frame] = (float)(sum / (stop - start));
      }

      return frames;
    }

    private static double QuantileSorted(
      IReadOnlyList<double> values,
      double quantile)
    {
      if (values.Count == 0) return 0;
      double position = Math.Clamp(quantile, 0, 1) *
        (values.Count - 1);
      int lower = (int)Math.Floor(position);
      int upper = Math.Min(values.Count - 1, lower + 1);
      double fraction = position - lower;
      return values[lower] * (1 - fraction) +
        values[upper] * fraction;
    }
  }
}
