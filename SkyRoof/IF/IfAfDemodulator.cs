using System;
using System.Numerics;

namespace SkyRoof.IF
{
  /// <summary>12-kHz real IF to mono AF. DSP is stream-continuous and CAT-independent.
  /// GNU Radio freq_xlating_fir_filter: mix down, reject image, select signed
  /// sideband. FM uses quadrature phase discrimination and post-demod LPF.
  /// Unlike a real product detector, a complex FIR rejects the opposite
  /// sideband rather than folding its noise into the wanted audio.</summary>
  public enum IfAfMode { Usb, Lsb, CwUsb, CwLsb, Fm, UsbDigital, LsbDigital }

  public sealed class IfAfDemodulator
  {
    public const int Rate = 48000;
    private const int SsbTaps = 257, FmTaps = 129, AfTaps = 95;
    private readonly Complex[] iqDelay = new Complex[SsbTaps];
    private readonly float[] rawDelay = new float[SsbTaps];
    private readonly double[][] realSsbWeights = new double[4][];
    private readonly Complex[] sideband = new Complex[SsbTaps];
    private readonly double[] fmFilter = new double[FmTaps];
    private readonly float[] audioDelay = new float[AfTaps];
    private readonly double[] afFilter = new double[AfTaps];
    private int write, audioWrite;
    private int phaseIndex;
    private Complex lastFm;
    private bool haveFm;
    private double lastFmPower;
    private double fmDc;
    private readonly double fmDeviation;
    private readonly int ssbLowHz, ssbHighHz;
    private readonly int cwCenterHz, cwBandwidthHz, fmIfCutoffHz, fmAfCutoffHz;
    private readonly IfAfCwAudioFilter cwAudio;
    private readonly IfAfVoiceEnhancements voice;
    private IfAfMode mode;
    public IfAfMode Mode => mode;
    public long SamplesProcessed { get; private set; }
    public long NonFiniteSamples { get; private set; }

    public IfAfDemodulator(IfAfMode mode = IfAfMode.Usb,
      double fmDeviationHz = 2500, int ssbLowHz = 180, int ssbHighHz = 3300,
      int cwCenterHz = 700, int cwBandwidthHz = 900,
      int fmIfCutoffHz = 8800, int fmAfCutoffHz = 3400,
      IfAfFmVoiceProcessing fmVoice = IfAfFmVoiceProcessing.Off,
      bool fmSquelch = false, int fmSquelchDbfs = -45,
      IfAfSsbVoiceNotch ssbVoiceNotch = IfAfSsbVoiceNotch.Off,
      IfAfSsbNoiseReduction ssbNoiseReduction = IfAfSsbNoiseReduction.Off)
    {
      // 6.25-kHz FM preset uses +/-900 Hz peak deviation. This guard
      // must cover every supported preset, or the receive worker faults
      // before it can produce any AF samples.
      if (fmDeviationHz is < 500 or > 6000)
        throw new ArgumentOutOfRangeException(nameof(fmDeviationHz));
      IfAfFilterSettings.ValidateAll(ssbLowHz, ssbHighHz, cwCenterHz,
        cwBandwidthHz, fmIfCutoffHz, fmAfCutoffHz);
      this.ssbLowHz = ssbLowHz;
      this.ssbHighHz = ssbHighHz;
      this.cwCenterHz = cwCenterHz;
      this.cwBandwidthHz = cwBandwidthHz;
      this.fmIfCutoffHz = fmIfCutoffHz;
      this.fmAfCutoffHz = fmAfCutoffHz;
      fmDeviation = fmDeviationHz;
      cwAudio = new IfAfCwAudioFilter(cwCenterHz, cwBandwidthHz);
      voice = new IfAfVoiceEnhancements(fmVoice, fmSquelch,
        fmSquelchDbfs, ssbVoiceNotch, ssbNoiseReduction);
      Configure(mode);
    }

    public void Configure(IfAfMode selected)
    {
      if (!Enum.IsDefined(typeof(IfAfMode), selected))
        throw new ArgumentOutOfRangeException(nameof(selected));
      mode = selected;
      double sign = selected is IfAfMode.Lsb or IfAfMode.CwLsb or
        IfAfMode.LsbDigital ? -1 : 1;
      bool cw = selected is IfAfMode.CwUsb or IfAfMode.CwLsb;
      // Keep a fixed, broad complex CW sideband preselector. Narrow CW
      // filtering is performed by the calibrated cascaded audio biquads;
      // applying both nominal bandwidths in series previously halved the
      // actual -3 dB width and exaggerated ringing at 100/200 Hz.
      bool digital = selected is IfAfMode.UsbDigital or IfAfMode.LsbDigital;
      double low = cw ? 50 : digital ? 100 : ssbLowHz;
      double high = cw ? 2300 : digital ? 3500 : ssbHighHz;
      double mid = sign * (low + high) / 2;
      double halfBand = (high - low) / 2;
      double[] baseband = Lowpass(SsbTaps, halfBand);
      for (int k = 0; k < sideband.Length; k++)
      {
        int d = k - (sideband.Length - 1) / 2;
        double angle = 2 * Math.PI * mid * d / Rate;
        sideband[k] = new Complex(baseband[k] * Math.Cos(angle),
          baseband[k] * Math.Sin(angle));
      }
      // Precompute signed real convolution weights for the four exact
      // quadrature NCO phases. This avoids 257 complex multiplications
      // per 48k sample and preserves the same phase-coherent waveform.
      for (int phase = 0; phase < 4; phase++)
      {
        var weights = new double[SsbTaps];
        for (int k = 0; k < SsbTaps; k++)
          weights[k] = ((phase - k) & 3) switch
          {
            0 => 2 * sideband[k].Real,
            1 => 2 * sideband[k].Imaginary,
            2 => -2 * sideband[k].Real,
            _ => -2 * sideband[k].Imaginary
          };
        realSsbWeights[phase] = weights;
      }
      Array.Copy(Lowpass(FmTaps, fmIfCutoffHz), fmFilter, FmTaps);
      Array.Copy(Lowpass(AfTaps, fmAfCutoffHz), afFilter, AfTaps);
      Reset();
    }

    /// <summary>Designs a normalized Blackman FIR with its -3 dB point at
    /// the requested cutoff, rather than using the sinc transition midpoint
    /// (~-6 dB) as the displayed bandwidth boundary.</summary>
    private static double[] Lowpass(int size, double cutoff)
    {
      const double minus3 = 0.7071067811865476;
      double lo = Math.Max(1, cutoff - 1200);
      double hi = Math.Min(Rate / 2.0 - 1, cutoff + 1200);
      for (int i = 0; i < 24; i++)
      {
        double mid = (lo + hi) / 2;
        double[] trial = DesignLowpass(size, mid);
        if (ResponseAt(trial, cutoff) > minus3) hi = mid;
        else lo = mid;
      }
      return DesignLowpass(size, (lo + hi) / 2);
    }

    private static double ResponseAt(double[] taps, double hz)
    {
      // Linear phase FIR: frequency response referenced to its midpoint
      // is purely real. This needs just one cosine sum at configuration.
      double center = (taps.Length - 1) / 2.0, sum = 0;
      for (int i = 0; i < taps.Length; i++)
        sum += taps[i] * Math.Cos(2 * Math.PI * hz * (i - center) / Rate);
      return Math.Abs(sum);
    }

    private static double[] DesignLowpass(int size, double cutoff)
    {
      double[] taps = new double[size];
      double sum = 0;
      for (int n = 0; n < size; n++)
      {
        int d = n - (size - 1) / 2;
        double sinc = d == 0 ? 2 * cutoff / Rate :
          Math.Sin(2 * Math.PI * cutoff * d / Rate) / (Math.PI * d);
        double window = 0.42 - .5 * Math.Cos(2 * Math.PI * n / (size - 1)) +
          .08 * Math.Cos(4 * Math.PI * n / (size - 1));
        taps[n] = sinc * window;
        sum += taps[n];
      }
      for (int n = 0; n < size; n++) taps[n] /= sum;
      return taps;
    }

    /// <summary>Does not allocate, clip, AGC or retune RF. Select FM deviation
    /// consistently with the receiver and stream configuration.</summary>
    public float Process(float sample)
    {
      if (!float.IsFinite(sample))
      {
        sample = 0;
        NonFiniteSamples++;
      }
      // 12k / 48k = 1/4; a quadrature NCO avoids trigonometric phase
      // accumulation error and preserves exact phase across block boundaries.
      int phaseNow = phaseIndex++ & 3;
      rawDelay[write] = sample;
      double value;
      if (mode != IfAfMode.Fm)
      {
        double sum = 0;
        double[] weights = realSsbWeights[phaseNow];
        int idx = write;
        for (int k = 0; k < SsbTaps; k++)
        {
          sum += weights[k] * rawDelay[idx];
          if (--idx < 0) idx = SsbTaps - 1;
        }
        value = sum;
        if (mode is IfAfMode.CwUsb or IfAfMode.CwLsb)
          value = cwAudio.Process(value);
        value = voice.Process(value, mode);
      }
      else
      {
        Complex mixed = phaseNow switch
        {
          0 => new Complex(2 * sample, 0),
          1 => new Complex(0, -2 * sample),
          2 => new Complex(-2 * sample, 0),
          _ => new Complex(0, 2 * sample)
        };
        iqDelay[write] = mixed;
        Complex filtered = Complex.Zero;
        int idx = write;
        for (int k = 0; k < FmTaps; k++)
        {
          filtered += fmFilter[k] * iqDelay[idx];
          if (--idx < 0) idx = SsbTaps - 1;
        }
        double raw = 0;
        double power = filtered.Real * filtered.Real +
          filtered.Imaginary * filtered.Imaginary;
        lastFmPower = power;
        if (haveFm && power > 1e-10 && lastFm.Magnitude * lastFm.Magnitude > 1e-10)
        {
          Complex delta = filtered * Complex.Conjugate(lastFm);
          raw = Math.Atan2(delta.Imaginary, delta.Real) *
            Rate / (2 * Math.PI * fmDeviation);
        }
        haveFm = power > 1e-10;
        lastFm = filtered;
        // Doppler/carrier offset is DC at the discriminator output.
        // A ~7.5-Hz DC blocker leaves 300–3000-Hz WSJT tones intact.
        double alpha = 1 - Math.Exp(-2 * Math.PI * 7.5 / Rate);
        fmDc += alpha * (raw - fmDc);
        audioDelay[audioWrite] = (float)(raw - fmDc);
        value = 0;
        idx = audioWrite;
        for (int k = 0; k < AfTaps; k++)
        {
          value += afFilter[k] * audioDelay[idx];
          if (--idx < 0) idx = AfTaps - 1;
        }
        if (++audioWrite == AfTaps) audioWrite = 0;
        value = voice.Process(value, mode, lastFmPower);
      }
      if (++write == SsbTaps) write = 0;
      SamplesProcessed++;
      return (float)(double.IsFinite(value) ? value : 0);
    }

    public void Process(ReadOnlySpan<float> input, Span<float> output)
    {
      if (output.Length < input.Length)
        throw new ArgumentException("AF buffer too small.", nameof(output));
      for (int n = 0; n < input.Length; n++) output[n] = Process(input[n]);
    }

    public void Reset()
    {
      Array.Clear(iqDelay);
      Array.Clear(rawDelay);
      Array.Clear(audioDelay);
      write = audioWrite = phaseIndex = 0;
      lastFm = Complex.Zero;
      fmDc = 0;
      lastFmPower = 0;
      haveFm = false;
      voice.Reset();
      cwAudio.Reset();
      SamplesProcessed = NonFiniteSamples = 0;
    }
  }
}
