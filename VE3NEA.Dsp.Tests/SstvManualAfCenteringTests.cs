using System;
using System.Collections.Generic;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  public sealed class SstvManualAfCenteringTests
  {
    [Theory]
    [InlineData(0x88, 300.0)]
    [InlineData(0x88, -280.0)]
    [InlineData(0xAC, 300.0)]
    public void ManualPitchCorrectionEnablesVisDetectionBeyondAutoAcquisitionSpan(
      int visByte, double incomingOffsetHz)
    {
      var pcm = new List<float>(48000 * 2);
      double phase = 0;
      void Tone(double frequency, int milliseconds)
      {
        for (int i = 0; i < milliseconds * 48; i++)
        {
          if (frequency != 0)
          {
            phase += 2 * Math.PI * (frequency + incomingOffsetHz) / 48000;
            if (phase >= 2 * Math.PI) phase -= 2 * Math.PI;
          }
          pcm.Add(frequency == 0 ? 0f : (float)(.75 * Math.Sin(phase)));
        }
      }
      Tone(0, 200);
      Tone(1900, 300);
      Tone(1200, 10);
      Tone(1900, 300);
      Tone(1200, 30);
      for (int bit = 0; bit < 8; bit++)
        Tone((visByte & (1 << bit)) != 0 ? 1100 : 1300, 30);
      Tone(1200, 30);
      Tone(1500, 200);

      // Raw VIS is outside the deliberate ±160-Hz auto-acquisition guard.
      Assert.Null(SstvVisModeIdentifier.DetectAudio(pcm.ToArray()));

      var shifter = new SstvAfFrequencyShifter();
      var corrected = new float[pcm.Count];
      for (int i = 0; i < corrected.Length; i++)
        corrected[i] = shifter.Process(pcm[i], -incomingOffsetHz);

      var recovered = SstvVisModeIdentifier.DetectAudio(corrected);
      Assert.True(recovered.HasValue,
        "Manual tuning must reach both Robot/PD and extended RGB VIS receivers.");
      Assert.Equal(visByte == 0xAC ? "Martin 1" : "Robot 36",
        recovered.Value.Profile.Name);
      Assert.InRange(recovered.Value.LeadingAudio.TotalMilliseconds, 195, 215);
      Assert.InRange(recovered.Value.FrequencyOffsetHz, -12, 12);
    }
  }
}
