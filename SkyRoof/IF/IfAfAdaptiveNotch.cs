using System;

namespace SkyRoof.IF
{
  /// <summary>
  /// Optional *voice-only* stable-tone interference notch. Input is 48k AF;
  /// spectral detection runs on 1-in-4 decimated AF samples in 2048-sample
  /// windows (~171 ms). Three successive windows must agree within 15 Hz
  /// and exceed the local median power by >=20 dB. It must never run for
  /// CW or digital USB-D/LSB-D. This is not broadband noise reduction.
  /// </summary>
  internal sealed class IfAfAdaptiveNotch
  {
    private const int Decimation = 4, SpectrumRate = 12000, WindowSize = 2048;
    private readonly double[] window = new double[WindowSize];
    private readonly double[] hann = new double[WindowSize];
    private readonly double[] powers = new double[370];
    private readonly double[] ordered = new double[370];
    private int downsamplePhase, windowCount, stableWindows, absentWindows;
    private double candidateHz, notchHz, blend, z1, z2;
    private double b0, b1, b2, a1, a2;

    public IfAfAdaptiveNotch()
    {
      for (int i = 0; i < WindowSize; i++)
        hann[i] = 0.5 - 0.5 *
          Math.Cos(2 * Math.PI * i / (WindowSize - 1));
    }

    public double NotchHz => notchHz;
    public void Reset()
    {
      downsamplePhase = windowCount = stableWindows = absentWindows = 0;
      candidateHz = notchHz = blend = z1 = z2 = 0;
      Array.Clear(window);
    }

    public double Process(double x)
    {
      if (++downsamplePhase == Decimation)
      {
        downsamplePhase = 0;
        window[windowCount] = x * hann[windowCount];
        windowCount++;
        if (windowCount == WindowSize)
        {
          windowCount = 0;
          ScanWindow();
        }
      }
      double target = notchHz > 0 ? 1 : 0;
      blend += (target - blend) * (1 - Math.Exp(-1.0 / (0.04 * 48000)));
      if (notchHz <= 0 && blend < 1e-5) return x;
      double filtered = b0 * x + z1;
      z1 = b1 * x - a1 * filtered + z2;
      z2 = b2 * x - a2 * filtered;
      return x + blend * (filtered - x);
    }

    private void ScanWindow()
    {
      // Goertzel at exact DFT bins k=86..443 (504..2596 Hz).
      // 358 bins * 2048 samples / ~171ms, only when AutoTracking selected.
      const int minK = 86, maxK = 443;
      int count = 0, bestK = 0;
      double bestPower = 0;
      for (int k = minK; k <= maxK; k++)
      {
        double w = 2 * Math.PI * k / WindowSize;
        double coeff = 2 * Math.Cos(w), q1 = 0, q2 = 0;
        for (int n = 0; n < WindowSize; n++)
        {
          // The Hann window is already applied on buffer insertion.
          double sample = window[n];
          double q = sample + coeff * q1 - q2;
          q2 = q1; q1 = q;
        }
        double power = Math.Max(0, q1 * q1 + q2 * q2 - coeff * q1 * q2);
        powers[count++] = power;
        if (power > bestPower) { bestPower = power; bestK = k; }
      }
      Array.Copy(powers, ordered, count);
      Array.Sort(ordered, 0, count);
      double median = Math.Max(ordered[count / 2], 1e-12);
      double frequency = bestK * SpectrumRate / (double)WindowSize;
      bool outstanding = bestPower > median * 100 &&
        bestPower > 1e-6;
      if (outstanding)
      {
        absentWindows = 0;
        if (Math.Abs(frequency - candidateHz) < 15)
          stableWindows++;
        else
        {
          candidateHz = frequency;
          stableWindows = 1;
        }
        if (stableWindows >= 3 &&
            Math.Abs(notchHz - candidateHz) >= 15)
          SetNotch(candidateHz);
      }
      else
      {
        stableWindows = 0;
        if (++absentWindows >= 8) SetNotch(0);
      }
    }

    private void SetNotch(double hz)
    {
      if (hz <= 0)
      {
        // Fade the old biquad out instead of switching to dry instantly.
        notchHz = 0;
        return;
      }
      notchHz = hz;
      z1 = z2 = 0;
      blend = 0;
      double omega = 2 * Math.PI * hz / 48000;
      double alpha = Math.Sin(omega) / (2 * (hz / 45.0));
      double a0 = 1 + alpha;
      b0 = b2 = 1 / a0;
      b1 = -2 * Math.Cos(omega) / a0;
      a1 = b1;
      a2 = (1 - alpha) / a0;
    }
  }
}
