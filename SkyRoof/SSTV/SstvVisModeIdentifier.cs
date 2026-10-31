using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Linq;
using NAudio.Wave;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  public readonly record struct SstvVisModeIdentity(
    SstvTxModeProfile Profile, TimeSpan LeadingAudio,
    double FrequencyOffsetHz, string HeaderFormat);

  /// <summary>
  /// Identify the MMSSTV mode before decoding recorded PCM, including 8-bit
  /// VIS, 16-bit extended VIS (0x23 lead) and narrowband 24-bit N-VIS.
  /// A valid leader/break/start plus mode-specific stop/parity/check fields
  /// is required; a single narrow spectral peak never determines the mode.
  /// </summary>
  public static class SstvVisModeIdentifier
  {
    private const int SamplesPerFrame = 240; // 5 ms @48k
    private const int FrameMs = 5;
    public static SstvVisModeIdentity? DetectWave(string wavePath)
    {
      using var wave = new WaveFileReader(wavePath);
      if (wave.WaveFormat.SampleRate != 48000 ||
          wave.WaveFormat.Channels != 1 ||
          wave.WaveFormat.BitsPerSample != 16)
        throw new InvalidDataException("仅支持 48 kHz / 16-bit / 单声道 SSTV WAV 录音。");
      // A normal receiver may have leading silence or speech. Search the
      // first 8 seconds, including full extended (1.15-s) headers.
      int maxSamples = (int)Math.Min(wave.Length / 2, 48000 * 8L);
      byte[] pcm = new byte[maxSamples * 2];
      int total = 0;
      while (total < pcm.Length)
      {
        int got = wave.Read(pcm, total, pcm.Length - total);
        if (got == 0) break;
        total += got;
      }
      int frames = total / (2 * SamplesPerFrame);
      var pitches = new double[frames];
      var scratch = new float[SamplesPerFrame];
      for (int i = 0; i < frames; i++)
      {
        int byteStart = 2 * i * SamplesPerFrame;
        for (int n = 0; n < SamplesPerFrame; n++)
        {
          int at = byteStart + 2 * n;
          short sample = (short)(pcm[at] | pcm[at + 1] << 8);
          scratch[n] = sample / 32768f;
        }
        pitches[i] = SstvVisAfcDetector.EstimatePitch(scratch);
      }
      return FindStandard(pitches) ?? FindNarrow(pitches);
    }

    private static SstvVisModeIdentity? FindStandard(double[] pitches)
    {
      for (int f = 0; f + 182 < pitches.Length; f++)
      {
        double offset = Mean(pitches, f + 8, f + 52) - 1900;
        if (!double.IsFinite(offset) || Math.Abs(offset) > 160 ||
            !Match(pitches, f + 10, f + 52, 1900 + offset, 85, .85) ||
            !Match(pitches, f + 60, f + 62, 1200 + offset, 90, .5) ||
            !Match(pitches, f + 72, f + 110, 1900 + offset, 85, .85) ||
            !Match(pitches, f + 123, f + 127, 1200 + offset, 85, .75))
          continue;

        int lo = ReadVisBits(pitches, f + 128, 8, offset);
        if (lo < 0) continue;
        if ((BitOperations.PopCount((uint)lo) & 1) == 0 &&
            Match(pitches, f + 178, f + 182, 1200 + offset, 85, .75))
        {
          SstvTxModeProfile? mode = FromVisByte(lo);
          if (mode != null)
            return new SstvVisModeIdentity(mode, TimeSpan.FromMilliseconds(f * FrameMs),
              offset, "VIS-8");
        }

        // MMSSTV 16-bit extension uses deliberately ODD low byte 0x23,
        // not a conventional 7+parity VIS byte.
        if (lo != 0x23 || f + 230 >= pitches.Length) continue;
        int hi = ReadVisBits(pitches, f + 176, 8, offset);
        if (hi < 0 || !Match(pitches, f + 226, f + 230,
          1200 + offset, 85, .75)) continue;
        int extended = lo | (hi << 8);
        SstvExtraTxSpec? extra = SstvExtraTxSpec.All.FirstOrDefault(
          x => x.Extended && x.VisCode == extended);
        if (extra == null) continue;
        SstvTxModeProfile? profile = SstvTxModeProfile.All.FirstOrDefault(
          x => x.Extra == extra);
        if (profile != null)
          return new SstvVisModeIdentity(profile,
            TimeSpan.FromMilliseconds(f * FrameMs), offset, "VIS-16");
      }
      return null;
    }

    private static SstvVisModeIdentity? FindNarrow(double[] p)
    {
      for (int f = 0; f + 191 < p.Length; f++)
      {
        double offset = Mean(p, f + 8, f + 52) - 1900;
        if (!double.IsFinite(offset) || Math.Abs(offset) > 160 ||
            !Match(p, f + 10, f + 52, 1900 + offset, 85, .85) ||
            !Match(p, f + 64, f + 76, 2100 + offset, 85, .75) ||
            !Match(p, f + 81, f + 84, 1900 + offset, 85, .5))
          continue;

        int[] groups = new int[4];
        bool valid = true;
        for (int group = 0; group < 4 && valid; group++)
          for (int bit = 0; bit < 6; bit++)
          {
            double midpointMs = 422 + (group * 6 + bit + .5) * 22;
            int center = f + (int)Math.Round(midpointMs / FrameMs);
            if (center < 0 || center >= p.Length) { valid = false; break; }
            int value = NarrowBit(p[center], offset);
            if (value < 0) { valid = false; break; }
            groups[group] = (groups[group] << 1) | value;
          }
        if (!valid || groups[0] != 0x2D || groups[1] != 0x15 ||
            groups[3] != (groups[2] ^ 0x15))
          continue;
        SstvExtraTxSpec? mode = SstvExtraTxSpec.All.FirstOrDefault(
          x => x.Narrow && x.VisCode == groups[2]);
        SstvTxModeProfile? profile = SstvTxModeProfile.All.FirstOrDefault(
          x => x.Extra == mode);
        if (profile != null)
          return new SstvVisModeIdentity(profile,
            TimeSpan.FromMilliseconds(f * FrameMs), offset, "N-VIS-24");
      }
      return null;
    }

    private static int NarrowBit(double hz, double offset) =>
      Math.Abs(hz - (1900 + offset)) < 85 ? 1 :
      Math.Abs(hz - (2100 + offset)) < 85 ? 0 : -1;

    private static int ReadVisBits(double[] p, int start, int bits, double offset)
    {
      int value = 0;
      for (int i = 0; i < bits; i++)
      {
        int at = start + 6 * i + 3;
        if (at >= p.Length) return -1;
        double hz = p[at];
        int bit = Math.Abs(hz - (1100 + offset)) < 80 ? 1 :
                  Math.Abs(hz - (1300 + offset)) < 80 ? 0 : -1;
        if (bit < 0) return -1;
        value |= bit << i;
      }
      return value;
    }

    private static double Mean(double[] p, int start, int end)
    {
      double sum = 0; int count = 0;
      for (int i = start; i < end && i < p.Length; i++)
        if (double.IsFinite(p[i])) { sum += p[i]; count++; }
      return count < (end - start) * .8 ? double.NaN : sum / count;
    }

    private static bool Match(double[] p, int start, int end,
      double freq, double tolerance, double fraction)
    {
      int correct = 0;
      for (int i = start; i < end && i < p.Length; i++)
        if (double.IsFinite(p[i]) && Math.Abs(p[i] - freq) <= tolerance)
          correct++;
      return correct >= Math.Ceiling((end - start) * fraction);
    }

    private static SstvTxModeProfile? FromVisByte(int code)
    {
      foreach (SstvTxModeProfile profile in SstvTxModeProfile.All)
      {
        if (profile.Native is { } native &&
            SstvModes.Get(native).VisByte == code) return profile;
        if (profile.Rgb is { } rgb &&
            SstvModes.EvenParityByte(rgb.VisCode7) == code) return profile;
        if (profile.Extra is { } extra && !extra.Extended && !extra.Narrow &&
            SstvModes.EvenParityByte(extra.VisCode) == code) return profile;
      }
      return null;
    }
  }
}
