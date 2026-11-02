using System;

namespace SkyRoof.IF
{
  /// <summary>
  /// Two cascaded constant-peak-gain CW audio bandpass biquads. This provides
  /// useful narrow CW selectivity without running thousands of FIR taps at
  /// 48 kS/s. Coarse opposite-sideband rejection remains in the complex FIR.
  /// Coefficients are fixed for the lifetime of one IF-AF configuration.
  /// </summary>
  internal sealed class IfAfCwAudioFilter
  {
    private readonly Biquad first, second;

    public IfAfCwAudioFilter(int centerHz, int bandwidthHz)
    {
      IfAfFilterSettings.ValidateCw(centerHz, bandwidthHz);
      double omega = 2 * Math.PI * centerHz / IfAfDemodulator.Rate;
      // Two equal RBJ bandpasses in cascade narrow the -3 dB width to
      // roughly 64% of a single section. With the broad complex CW
      // preselector, 1.56 compensates this cascade for all UI presets
      // (100..1000 Hz). A response-grid regression test verifies this.
      double q = (double)centerHz / (bandwidthHz * 1.56);
      double alpha = Math.Sin(omega) / (2 * q);
      double a0 = 1 + alpha;
      // RBJ audio EQ cookbook: constant-peak-gain bandpass, cascade 2 poles
      // twice for steeper skirts. Neither branch feeds the raw IF/AFC path.
      double b0 = alpha / a0;
      double a1 = -2 * Math.Cos(omega) / a0;
      double a2 = (1 - alpha) / a0;
      first = new Biquad(b0, 0, -b0, a1, a2);
      second = new Biquad(b0, 0, -b0, a1, a2);
    }

    public double Process(double value) => second.Process(first.Process(value));
    public void Reset() { first.Reset(); second.Reset(); }

    private sealed class Biquad
    {
      private readonly double b0, b1, b2, a1, a2;
      private double z1, z2;

      public Biquad(double b0, double b1, double b2, double a1, double a2)
      {
        this.b0 = b0; this.b1 = b1; this.b2 = b2;
        this.a1 = a1; this.a2 = a2;
      }

      public double Process(double x)
      {
        double y = b0 * x + z1;
        z1 = b1 * x - a1 * y + z2;
        z2 = b2 * x - a2 * y;
        return y;
      }
      public void Reset() { z1 = z2 = 0; }
    }
  }

  /// <summary>Central filter-parameter validation shared by settings, service
  /// and DSP. Keeps invalid user settings away from the audio device.</summary>
  public static class IfAfFilterSettings
  {
    public static void ValidateSsb(int lowHz, int highHz)
    {
      if (lowHz is < 100 or > 750 ||
          highHz is < 1300 or > 4000 || highHz - lowHz < 1200)
        throw new ArgumentOutOfRangeException(nameof(highHz),
          "SSB passband: 100-750 Hz lower edge, 1300-4000 Hz upper edge, at least 1200 Hz wide.");
    }

    public static void ValidateCw(int centerHz, int bandwidthHz)
    {
      if (centerHz is < 300 or > 2500 ||
          bandwidthHz is < 100 or > 2500 ||
          centerHz - bandwidthHz / 2.0 < 100 ||
          centerHz + bandwidthHz / 2.0 > 4000)
        throw new ArgumentOutOfRangeException(nameof(bandwidthHz),
          "CW center: 300-2500 Hz; width: 100-2500 Hz; band must lie within 100-4000 Hz.");
    }

    public static void ValidateFm(int ifCutoffHz, int afCutoffHz)
    {
      if (ifCutoffHz is < 3000 or > 11000 ||
          afCutoffHz is < 1500 or > 5000)
        throw new ArgumentOutOfRangeException(nameof(ifCutoffHz),
          "FM complex IF cutoff: 3000-11000 Hz; audio cutoff: 1500-5000 Hz.");
    }

    public static void ValidateAll(int ssbLowHz, int ssbHighHz,
      int cwCenterHz, int cwBandwidthHz, int fmIfCutoffHz, int fmAfCutoffHz)
    {
      ValidateSsb(ssbLowHz, ssbHighHz);
      ValidateCw(cwCenterHz, cwBandwidthHz);
      ValidateFm(fmIfCutoffHz, fmAfCutoffHz);
    }
  }
}
