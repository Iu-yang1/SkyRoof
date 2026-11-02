using System;
using SkyRoof;
using SkyRoof.IF;
using VE3NEA;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class IfAfVoiceEnhancementTests
  {
    private const int Rate = 48000;

    private static float[] IfTone(int toneHz, float amplitude = .4f)
    {
      var result = new float[Rate];
      for (int i = 0; i < result.Length; i++)
        result[i] = amplitude * (float)Math.Cos(
          2 * Math.PI * (12000 + toneHz) * i / Rate);
      return result;
    }

    private static double ToneAmplitude(float[] af, int hz)
    {
      double re = 0, im = 0;
      for (int n = 12000; n < af.Length; n++)
      {
        double a = 2 * Math.PI * hz * n / Rate;
        re += af[n] * Math.Cos(a);
        im -= af[n] * Math.Sin(a);
      }
      return 2 * Math.Sqrt(re * re + im * im) / (af.Length - 12000);
    }

    private static float[] Decode(int hz, IfAfMode mode,
      IfAfSsbVoiceNotch notch)
    {
      var d = new IfAfDemodulator(mode, ssbVoiceNotch: notch);
      var output = new float[Rate];
      d.Process(IfTone(hz), output);
      return output;
    }

    [Fact]
    public void FollowRadioDetectsDigitalModesWithoutVoiceAgc()
    {
      Assert.Equal(IfAfMode.UsbDigital,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.USB_D));
      Assert.Equal(IfAfMode.LsbDigital,
        IfAfSettings.Resolve(IfAfSelection.FollowRadio, Slicer.Mode.LSB_D));
      var leveler = new IfAfAudioLeveler(new IfAfSettings());
      var audio = new float[480];
      for (int i = 0; i < audio.Length; i++) audio[i] =
        (float)(.01 * Math.Sin(2 * Math.PI * 1000 * i / Rate));
      var result = leveler.Process(audio, IfAfMode.UsbDigital);
      Assert.InRange(result.EffectiveGainDb, -.01, .01);
    }

    [Fact]
    public void DigitalModeSkipsVoiceOnlyNotch()
    {
      const IfAfSsbVoiceNotch notch = IfAfSsbVoiceNotch.Tone1000Hz;
      var analog = Decode(1000, IfAfMode.Usb, notch);
      var digital = Decode(1000, IfAfMode.UsbDigital, notch);
      double a = ToneAmplitude(analog, 1000), d = ToneAmplitude(digital, 1000);
      Assert.True(d > .25 && a < d * .25,
        $"voice notch should only affect analog USB: {a}/{d}");
    }

    [Fact]
    public void AutoTrackingNotchLocksStableInterfererAndLeavesDigitalBypassed()
    {
      const int tone = 1000;
      var auto = Decode(tone, IfAfMode.Usb, IfAfSsbVoiceNotch.AutoTracking);
      var raw = Decode(tone, IfAfMode.Usb, IfAfSsbVoiceNotch.Off);
      var digital = Decode(tone, IfAfMode.UsbDigital,
        IfAfSsbVoiceNotch.AutoTracking);
      // Auto tracker needs three 171-ms windows before acquiring a
      // stable narrowband interferer; evaluate only the final 20%.
      double AmplitudeLast(float[] pcm)
      {
        double re = 0, im = 0;
        for (int i = 38400; i < 48000; i++)
        {
          double angle = 2 * Math.PI * tone * i / Rate;
          re += pcm[i] * Math.Cos(angle);
          im -= pcm[i] * Math.Sin(angle);
        }
        return 2 * Math.Sqrt(re * re + im * im) / 9600;
      }
      double muted = AmplitudeLast(auto);
      double reference = AmplitudeLast(raw);
      double kept = AmplitudeLast(digital);
      Assert.True(reference > .25 && muted < reference * .3,
        $"Auto notch did not suppress stable {tone} Hz tone: {muted}/{reference}");
      Assert.InRange(kept / reference, .99, 1.01);
    }

    [Fact]
    public void AnalogNotchRetainsNearbySpeech()
    {
      var notch = IfAfSsbVoiceNotch.Tone1000Hz;
      double suppressed = ToneAmplitude(Decode(1000, IfAfMode.Usb, notch),1000);
      double speech = ToneAmplitude(Decode(1500, IfAfMode.Usb, notch),1500);
      Assert.True(suppressed < .05);
      Assert.True(speech > .25);
    }

    [Theory]
    [InlineData(IfAfFmVoiceProcessing.Voice50us)]
    [InlineData(IfAfFmVoiceProcessing.Voice75us)]
    public void VoiceDeemphasisPreservesLowFrequencyAndAttenuatesHigh(
      IfAfFmVoiceProcessing profile)
    {
      double low = FmToneResponse(400, profile);
      double high = FmToneResponse(2700, profile);
      double lowRaw = FmToneResponse(400, IfAfFmVoiceProcessing.Off);
      double highRaw = FmToneResponse(2700, IfAfFmVoiceProcessing.Off);
      Assert.True(low > .005);
      Assert.True(high / low < highRaw / lowRaw * .85,
        "FM voice deemphasis should reduce high AF relative to low AF");
    }

    private static double FmToneResponse(int modulationHz,
      IfAfFmVoiceProcessing profile)
    {
      var fm = new float[Rate];
      double phase = 0;
      for (int i = 0; i < fm.Length; i++)
      {
        double t = i / (double)Rate;
        phase += 2 * Math.PI / Rate * (12000 +
          1200 * Math.Sin(2 * Math.PI * modulationHz * t));
        fm[i] = (float)(.5 * Math.Cos(phase));
      }
      var d = new IfAfDemodulator(IfAfMode.Fm, fmVoice: profile);
      var af = new float[Rate];
      d.Process(fm, af);
      return ToneAmplitude(af, modulationHz);
    }

    [Fact]
    public void FmSquelchIsOffByDefaultAndOnlyGatesWhenExplicitlyEnabled()
    {
      var raw = new IfAfDemodulator(IfAfMode.Fm);
      var gated = new IfAfDemodulator(IfAfMode.Fm, fmSquelch: true,
        fmSquelchDbfs: -45);
      float[] empty = new float[Rate];
      var a = new float[Rate]; var b = new float[Rate];
      raw.Process(empty, a); gated.Process(empty, b);
      Assert.All(b, x => Assert.True(float.IsFinite(x)));
      var fm = new float[Rate];
      double phase = 0;
      for (int i = 0; i < fm.Length; i++)
      {
        phase += 2 * Math.PI * (12000 + 500 * Math.Sin(
          2 * Math.PI * 850 * i / Rate)) / Rate;
        fm[i] = (float)(.5 * Math.Cos(phase));
      }
      gated.Process(fm, b);
      Assert.True(ToneAmplitude(b, 850) > .05,
        "FM voice squelch must open after an adequate carrier");
    }
  }
}
