using System;

namespace SkyRoof.IF
{
  /// <summary>Post-demodulation AF control; never touches the raw IF shared
  /// with AFC/SSTV. SSB/CW use bounded slow RMS AGC. FM uses its independent
  /// deviation-normalized gain. A block-peak safety ceiling avoids hard
  /// clipping without adding a lookahead delay to the satellite audio path.</summary>
  public readonly record struct IfAfLevelResult(
    double OutputRmsDbfs, double PreLimitPeakDbfs,
    double EffectiveGainDb, double PeakReductionDb,
    bool PeakLimited, long ClippedSamples);

  public sealed class IfAfAudioLeveler
  {
    private const double Ceiling = 0.89; // -1.01 dBFS peak headroom
    private const double ActivityFloor = 0.0001; // -80 dBFS; do not AGC silence
    private readonly bool enableAgc;
    private readonly double ssbTrim, cwTrim, fmGain, targetRms, maxAgcGain;
    private IfAfMode? lastMode;
    private double rmsEnvelope;
    private double agcGain = 1;
    private bool agcAcquired;

    public IfAfAudioLeveler(IfAfSettings config)
    {
      ArgumentNullException.ThrowIfNull(config);
      if (config.SsbGainDb is < -40 or > 24 ||
          config.CwGainDb is < -40 or > 24 ||
          config.FmGainDb is < -40 or > 12 ||
          config.SsbCwAgcTargetDbfs is < -36 or > -12 ||
          config.SsbCwAgcMaxGainDb is < 0 or > 36)
        throw new ArgumentOutOfRangeException(nameof(config),
          "Invalid SSB/CW/FM trim or bounded AGC target/max gain.");
      ssbTrim = DbToLinear(config.SsbGainDb);
      cwTrim = DbToLinear(config.CwGainDb);
      fmGain = DbToLinear(config.FmGainDb);
      targetRms = DbToLinear(config.SsbCwAgcTargetDbfs);
      maxAgcGain = DbToLinear(config.SsbCwAgcMaxGainDb);
      enableAgc = config.SsbCwAgcEnabled;
    }

    public static double DbToLinear(double db) => Math.Pow(10, db / 20);

    public void Reset()
    {
      rmsEnvelope = 0;
      agcGain = 1;
      agcAcquired = false;
      lastMode = null;
    }

    public IfAfLevelResult Process(Span<float> audio, IfAfMode mode)
    {
      if (audio.IsEmpty) return new(double.NegativeInfinity,
        double.NegativeInfinity, 0, 0, false, 0);
      if (lastMode != mode)
      {
        Reset();
        lastMode = mode;
      }

      double power = 0, peak = 0;
      for (int i = 0; i < audio.Length; i++)
      {
        double value = float.IsFinite(audio[i]) ? audio[i] : 0;
        if (!float.IsFinite(audio[i])) audio[i] = 0;
        power += value * value;
        peak = Math.Max(peak, Math.Abs(value));
      }
      double rms = Math.Sqrt(power / audio.Length);
      double trim = mode switch
      {
        IfAfMode.Fm => fmGain,
        IfAfMode.CwUsb or IfAfMode.CwLsb => cwTrim,
        _ => ssbTrim
      };
      double requestedGain = trim;
      if (enableAgc && mode != IfAfMode.Fm)
      {
        // SSB/CW AGC release is deliberately slow to preserve FT4/FT8
        // symbol envelopes and avoid increasing gain in Morse key-up gaps.
        if (rms > ActivityFloor)
        {
          double duration = audio.Length / (double)IfAfDemodulator.Rate;
          double rmsTau = rms > rmsEnvelope ? .120 : 1.20;
          double alpha = 1 - Math.Exp(-duration / rmsTau);
          rmsEnvelope += alpha * (rms - rmsEnvelope);

          double desired = Math.Clamp(targetRms / Math.Max(rmsEnvelope,
            ActivityFloor), DbToLinear(-12), maxAgcGain);
          if (!agcAcquired)
          {
            agcGain = desired;
            agcAcquired = true;
          }
          else
          {
            double gainTau = desired < agcGain ? .040 : 1.50;
            double update = 1 - Math.Exp(-duration / gainTau);
            agcGain += update * (desired - agcGain);
          }
        }
        requestedGain *= agcGain;
      }

      // Measure how hot the signal would have been WITHOUT the ceiling,
      // then scale the entire block linearly. This avoids hard-clipped
      // samples and avoids the EVM/harmonic penalty of tanh soft-clipping.
      double requestedPeak = peak * requestedGain;
      double reduction = requestedPeak > Ceiling
        ? Ceiling / requestedPeak : 1;
      double finalGain = requestedGain * reduction;
      double deliveredPower = 0;
      long clipped = 0;
      for (int i = 0; i < audio.Length; i++)
      {
        double v = audio[i] * finalGain;
        if (v > 0.999 || v < -0.999)
        {
          clipped++;
          v = Math.Clamp(v, -0.999, 0.999);
        }
        audio[i] = (float)v;
        deliveredPower += v * v;
      }
      return new(
        10 * Math.Log10(Math.Max(deliveredPower / audio.Length, 1e-15)),
        20 * Math.Log10(Math.Max(requestedPeak, 1e-15)),
        20 * Math.Log10(Math.Max(finalGain, 1e-15)),
        -20 * Math.Log10(reduction),
        reduction < 0.999999,
        clipped);
    }
  }
}
