using System;
using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace SkyRoof.IF
{
  /// <summary>
  /// Optional analog-SSB mild spectral noise suppression, based on Hann
  /// WOLA and a per-frame median spectral floor estimate. 2048-sample
  /// frames, 50% overlap, <=8 dB bin attenuation. Bypassed entirely for
  /// CW, FM and USB-D/LSB-D. Introduces ~42.7 ms audio latency when enabled.
  /// NOT a substitute for a validated MMSE-LSA, RNNoise or a clean RF front end.
  /// </summary>
  internal sealed class IfAfSpectralNoiseReducer
  {
    private const int N = 2048, Hop = N / 2, OutputRingLength = 8192;
    private readonly double[] input = new double[N];
    private readonly double[] window = new double[N];
    private readonly double[] output = new double[OutputRingLength];
    private readonly double[] weight = new double[OutputRingLength];
    private readonly double[] powerSorted = new double[N / 2];
    private readonly double[] gain = new double[N / 2 + 1];
    private readonly Complex[] spectrum = new Complex[N];
    private long inputCount;

    internal IfAfSpectralNoiseReducer()
    {
      for (int i = 0; i < N; i++)
        window[i] = Math.Sqrt(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / N));
      Array.Fill(gain, 1);
    }

    internal void Reset()
    {
      Array.Clear(input);
      Array.Clear(output);
      Array.Clear(weight);
      Array.Fill(gain, 1);
      inputCount = 0;
    }

    internal double Process(double sample)
    {
      input[(int)(inputCount % N)] = double.IsFinite(sample) ? sample : 0;
      inputCount++;
      if (inputCount >= N && inputCount % Hop == 0)
        ProcessFrame(inputCount - N);
      long emit = inputCount - N - 1;
      if (emit < 0) return 0;
      int idx = (int)(emit % OutputRingLength);
      double result = weight[idx] > 1e-10 ? output[idx] / weight[idx] : 0;
      output[idx] = weight[idx] = 0;
      return double.IsFinite(result) ? result : 0;
    }

    private void ProcessFrame(long start)
    {
      for (int n = 0; n < N; n++)
        spectrum[n] = new Complex(input[(int)((start + n) % N)] * window[n], 0);
      Fourier.Forward(spectrum, FourierOptions.Matlab);

      int count = 0;
      // Estimate the stationary noise floor INSIDE the useful SSB audio
      // band (roughly 280..3400 Hz). Including all Nyquist bins would
      // be dominated by the existing SSB channel filter's stopband zeros
      // and make median noise power nearly zero (no suppression).
      for (int k = 12; k <= 145; k++)
        powerSorted[count++] = spectrum[k].Magnitude *
          spectrum[k].Magnitude;
      Array.Sort(powerSorted, 0, count);
      double median = Math.Max(1e-12, powerSorted[count / 2]);

      for (int k = 1; k < N / 2; k++)
      {
        double power = spectrum[k].Magnitude * spectrum[k].Magnitude;
        double desired = Math.Clamp(1 - 1.4 * median /
          Math.Max(power, 1e-15), .40, 1);
        // Time smoothing limits musical noise; spectral bins containing
        // strong speech are preserved while bins near the floor are
        // attenuated by at most ~8 dB.
        gain[k] = .72 * gain[k] + .28 * desired;
        spectrum[k] *= gain[k];
        spectrum[N - k] *= gain[k];
      }
      Fourier.Inverse(spectrum, FourierOptions.Matlab);
      for (int i = 0; i < N; i++)
      {
        int idx = (int)((start + i) % OutputRingLength);
        output[idx] += spectrum[i].Real * window[i];
        weight[idx] += window[i] * window[i];
      }
    }
  }
}
