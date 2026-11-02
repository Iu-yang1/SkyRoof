using System;

namespace SkyRoof.IF
{
  /// <summary>Optional local voice-only enhancements. Digital USB-D/LSB-D,
  /// CW, AFC and raw IF never pass through this processor.</summary>
  internal sealed class IfAfVoiceEnhancements
  {
    private readonly IfAfFmVoiceProcessing fmVoice;
    private readonly bool fmSquelch;
    private readonly double fmPowerThreshold;
    private readonly IfAfSsbVoiceNotch ssbNotch;
    private readonly IfAfAdaptiveNotch? adaptiveNotch;
    private readonly double nb0, nb1, nb2, na1, na2;
    private double nz1, nz2;
    private double fmSmoothPower, fmGate = 1, deemph;
    private IfAfMode? previousMode;

    internal IfAfVoiceEnhancements(IfAfFmVoiceProcessing fmVoice,
      bool fmSquelch, int fmSquelchDbfs, IfAfSsbVoiceNotch ssbNotch)
    {
      if (!Enum.IsDefined(typeof(IfAfFmVoiceProcessing), fmVoice) ||
          !Enum.IsDefined(typeof(IfAfSsbVoiceNotch), ssbNotch) ||
          fmSquelchDbfs is < -95 or > -10)
        throw new ArgumentOutOfRangeException(nameof(fmSquelchDbfs));
      this.fmVoice = fmVoice;
      this.fmSquelch = fmSquelch;
      fmPowerThreshold = Math.Pow(10, fmSquelchDbfs / 10.0);
      this.ssbNotch = ssbNotch;
      if (ssbNotch == IfAfSsbVoiceNotch.AutoTracking)
        adaptiveNotch = new IfAfAdaptiveNotch();
      int hz = ssbNotch switch
      {
        IfAfSsbVoiceNotch.Tone1000Hz => 1000,
        IfAfSsbVoiceNotch.Tone1500Hz => 1500,
        _ => 0
      };
      if (hz > 0)
      {
        double omega = 2 * Math.PI * hz / IfAfDemodulator.Rate;
        double alpha = Math.Sin(omega) / (2 * (hz / 45.0));
        double a0 = 1 + alpha;
        nb0 = nb2 = 1 / a0;
        nb1 = -2 * Math.Cos(omega) / a0;
        na1 = nb1;
        na2 = (1 - alpha) / a0;
      }
    }

    internal void Reset()
    {
      nz1 = nz2 = deemph = fmSmoothPower = 0;
      fmGate = fmSquelch ? 0 : 1;
      previousMode = null;
      adaptiveNotch?.Reset();
    }

    internal double Process(double sample, IfAfMode mode, double fmIqPower = 0)
    {
      if (mode != previousMode)
      {
        Reset();
        previousMode = mode;
      }
      if ((mode is IfAfMode.Usb or IfAfMode.Lsb) &&
          ssbNotch == IfAfSsbVoiceNotch.AutoTracking)
        return adaptiveNotch!.Process(sample);
      if ((mode is IfAfMode.Usb or IfAfMode.Lsb) &&
          ssbNotch != IfAfSsbVoiceNotch.Off)
      {
        double y = nb0 * sample + nz1;
        nz1 = nb1 * sample - na1 * y + nz2;
        nz2 = nb2 * sample - na2 * y;
        return y;
      }

      if (mode != IfAfMode.Fm) return sample;
      if (fmVoice != IfAfFmVoiceProcessing.Off)
      {
        // First-order FM voice de-emphasis (50/75 us). Keep the gain at DC
        // unity; optional voice-only path, never applied to data.
        double tau = fmVoice == IfAfFmVoiceProcessing.Voice50us
          ? 50e-6 : 75e-6;
        double alpha = 1 - Math.Exp(-1 / (tau * IfAfDemodulator.Rate));
        deemph += alpha * (sample - deemph);
        sample = deemph;
      }

      if (fmSquelch)
      {
        // ~50 ms RF power envelope, with 3 dB close hysteresis and 10 ms
        // audio gate ramp. Sample-exact zero output when fully closed.
        double a = 1 - Math.Exp(-1.0 / (0.05 * IfAfDemodulator.Rate));
        fmSmoothPower += a * (Math.Max(0, fmIqPower) - fmSmoothPower);
        double closePower = fmPowerThreshold / 2;
        bool opening = fmGate > 0.01
          ? fmSmoothPower >= closePower
          : fmSmoothPower >= fmPowerThreshold;
        double target = opening ? 1 : 0;
        double step = 1.0 / (0.010 * IfAfDemodulator.Rate);
        fmGate = Math.Clamp(fmGate + Math.Sign(target - fmGate) * step, 0, 1);
        sample *= fmGate;
      }
      return sample;
    }
  }
}
