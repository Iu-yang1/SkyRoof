using System;
using System.Drawing;
using System.IO;
using NAudio.Wave;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// A continuously clocked analytic FM-subcarrier discriminator. A triple
  /// complex low-pass after 1900-Hz downmix removes the 3800-Hz real-signal
  /// image while retaining the SSTV 1500–2300-Hz instantaneous frequency.
  /// This path is distinct from the rig's RF/IF CAT calibration.
  /// </summary>
  public sealed class SstvAfToneDiscriminator
  {
    private const double Fs = 48000;
    private const double Alpha = .12;
    private const double Omega = 2 * Math.PI * 1900 / Fs;
    private static readonly double StepCos = Math.Cos(Omega);
    private static readonly double StepSin = Math.Sin(Omega);
    private double oscCos = 1, oscSin;
    private double aI, aQ, bI, bQ, cI, cQ;
    private double lastI, lastQ;
    private int count;

    public double Process(float sample)
    {
      double real = 2 * sample * oscCos;
      double imag = -2 * sample * oscSin;
      double cosine = oscCos * StepCos - oscSin * StepSin;
      double sine = oscCos * StepSin + oscSin * StepCos;
      oscCos = cosine;
      oscSin = sine;
      if (++count % 48000 == 0)
      {
        double norm = Math.Sqrt(oscCos * oscCos + oscSin * oscSin);
        oscCos /= norm;
        oscSin /= norm;
      }
      aI += Alpha * (real - aI);
      aQ += Alpha * (imag - aQ);
      bI += Alpha * (aI - bI);
      bQ += Alpha * (aQ - bQ);
      cI += Alpha * (bI - cI);
      cQ += Alpha * (bQ - cQ);

      double phaseDelta = Math.Atan2(cQ * lastI - cI * lastQ,
        cI * lastI + cQ * lastQ);
      lastI = cI;
      lastQ = cQ;
      double hz = 1900 + phaseDelta * Fs / (2 * Math.PI);
      return count > 128 && double.IsFinite(hz) && hz is >= 950 and <= 2400 ?
        hz : 1900;
    }
  }

  /// <summary>
  /// Manual-mode recorded-wave raster receiver for the extended MMSSTV
  /// families. It reads only one row of audio at a time rather than
  /// allocating an entire 7-minute demodulated PCM buffer. This method
  /// assumes the selected mode and that the WAV starts at the VIS leader;
  /// live VIS acquisition and slant calibration remain separate tasks.
  /// </summary>
  public static class SstvOfflineRasterDecoder
  {
    public static Bitmap Decode(string wavPath, SstvTxModeProfile profile,
      TimeSpan leadingAudio = default, double pitchCorrectionHz = 0)
    {
      ArgumentNullException.ThrowIfNull(profile);
      using var wave = new WaveFileReader(wavPath);
      if (wave.WaveFormat.SampleRate != 48000 ||
          wave.WaveFormat.Channels != 1 ||
          wave.WaveFormat.BitsPerSample != 16)
        throw new InvalidDataException("接收录音必须为 48 kHz / 16-bit / 单声道 WAV。");

      double header, lineMs;
      int rowsPerScan = 1;
      int lines;
      if (profile.Rgb is { } rgb)
      {
        header = SstvTones.VisHeaderMs +
          (rgb.Layout == SstvRgbTxLayout.Scottie ? 9 : 0);
        lineMs = rgb.LineMs;
        lines = rgb.Height;
      }
      else if (profile.Extra is { } extra)
      {
        header = extra.HeaderMs;
        lineMs = extra.LineMs;
        rowsPerScan = extra.RowsPerScan;
        lines = extra.LineCount;
      }
      else
        throw new NotSupportedException(
          "Robot/PD live receiver is provided by SkySignals; select another manual decode mode.");

      if (leadingAudio < TimeSpan.Zero || leadingAudio.TotalSeconds > 120 ||
          !double.IsFinite(pitchCorrectionHz) || Math.Abs(pitchCorrectionHz) > 2000)
        throw new ArgumentOutOfRangeException(nameof(pitchCorrectionHz));
      double leadingMs = leadingAudio.TotalMilliseconds;
      double expected = (leadingMs + header + lines * lineMs) / 1000;
      if (wave.TotalTime.TotalSeconds + .02 < expected)
        throw new InvalidDataException(
          "录音时长不足以解码所选 SSTV 模式的完整图像。");

      int discarded = checked((int)Math.Round((leadingMs + header) * 48) * 2);
      byte[] skip = new byte[8192];
      while (discarded > 0)
      {
        int read = wave.Read(skip, 0, Math.Min(skip.Length, discarded));
        if (read <= 0) throw new EndOfStreamException("SSTV header truncated.");
        discarded -= read;
      }
      var demod = new SstvAfToneDiscriminator();
      var picture = new Bitmap(profile.Width, profile.Height);
      try
      {
        long previous = (long)Math.Round((leadingMs + header) * 48);
        for (int line = 0; line < lines; line++)
        {
          long next = (long)Math.Round((leadingMs + header + (line + 1) * lineMs) * 48);
          int samples = checked((int)(next - previous));
          previous = next;
          byte[] bytes = new byte[samples * 2];
          int offset = 0;
          while (offset < bytes.Length)
          {
            int got = wave.Read(bytes, offset, bytes.Length - offset);
            if (got <= 0) throw new EndOfStreamException("SSTV raster truncated.");
            offset += got;
          }
          var hz = new float[samples];
          for (int n = 0; n < samples; n++)
          {
            short pcm = (short)(bytes[2 * n] | bytes[2 * n + 1] << 8);
            hz[n] = (float)(demod.Process(pcm / 32768f) - pitchCorrectionHz);
          }

          RenderLine(picture, profile, line, hz);
        }
        return picture;
      }
      catch
      {
        picture.Dispose();
        throw;
      }
    }

    internal static void RenderLine(Bitmap picture,
      SstvTxModeProfile profile, int line, float[] hz)
    {
      int rowsPerScan = profile.Extra?.RowsPerScan ?? 1;
              int width = profile.Width;
              double[] red, green, blue;
              double[]? nextY = null;
              bool chroma = false;
              if (profile.Rgb is { } rgbLine)
              {
                (red, green, blue) = DecodeRgbLine(hz, rgbLine, width);
              }
              else
              {
                var spec = profile.Extra!;
                (red, green, blue, nextY, chroma) = DecodeExtraLine(hz, spec, width);
              }
              int row = line * rowsPerScan;
              for (int x = 0; x < width; x++)
              {
                if (chroma)
                {
                  var (r, g, b) = YCrCb.ToRgb(red[x], green[x], blue[x]);
                  picture.SetPixel(x, row, Color.FromArgb(Round(r), Round(g), Round(b)));
                  if (rowsPerScan == 2 && row + 1 < profile.Height)
                  {
                    (r, g, b) = YCrCb.ToRgb(nextY?[x] ?? red[x], green[x], blue[x]);
                    picture.SetPixel(x, row + 1, Color.FromArgb(Round(r), Round(g), Round(b)));
                  }
                }
                else
                {
                  var pixel = Color.FromArgb(Round(red[x]), Round(green[x]), Round(blue[x]));
                  picture.SetPixel(x, row, pixel);
                  if (rowsPerScan == 2 && row + 1 < profile.Height)
                    picture.SetPixel(x, row + 1, pixel);
                }
              }
    }

    private static (double[] r, double[] g, double[] b)
      DecodeRgbLine(float[] hz, SstvRgbTxSpec spec, int width)
    {
      double at = 0;
      double[] r, g, b;
      if (spec.Layout == SstvRgbTxLayout.Scottie)
      {
        at += 1.5;
        g = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs + 1.5;
        b = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs + 9 + 1.5;
        r = Scan(hz, at, spec.ScanMs, width);
      }
      else if (spec.Layout == SstvRgbTxLayout.Martin)
      {
        at = spec.SyncMs + spec.PorchMs;
        g = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs + spec.PorchMs;
        b = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs + spec.PorchMs;
        r = Scan(hz, at, spec.ScanMs, width);
      }
      else
      {
        at = spec.SyncMs + spec.PorchMs;
        r = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs;
        g = Scan(hz, at, spec.ScanMs, width); at += spec.ScanMs;
        b = Scan(hz, at, spec.ScanMs, width);
      }
      return (r, g, b);
    }

    private static (double[] r, double[] g, double[] b, double[]? y2, bool chroma)
      DecodeExtraLine(float[] hz, SstvExtraTxSpec mode, int width)
    {
      double at = mode.SyncMs + mode.PorchMs;
      double[] r, g, b;
      switch (mode.Layout)
      {
        case SstvExtraLayout.P:
          r = Scan(hz, at, mode.ScanMs, width); at += mode.ScanMs + mode.PorchMs;
          g = Scan(hz, at, mode.ScanMs, width); at += mode.ScanMs + mode.PorchMs;
          b = Scan(hz, at, mode.ScanMs, width);
          return (r, g, b, null, false);

        case SstvExtraLayout.Mp:
        case SstvExtraLayout.NarrowMp:
          bool narrow = mode.Narrow;
          r = Scan(hz, at, mode.ScanMs, width, narrow); at += mode.ScanMs;
          g = Scan(hz, at, mode.ScanMs, width, narrow); at += mode.ScanMs;
          b = Scan(hz, at, mode.ScanMs, width, narrow); at += mode.ScanMs;
          return (r, g, b, Scan(hz, at, mode.ScanMs, width, narrow), true);

        case SstvExtraLayout.Mr:
          r = Scan(hz, at, mode.ScanMs, width); at += mode.ScanMs + .1;
          g = Scan(hz, at, mode.ScanMs / 2, width); at += mode.ScanMs / 2 + .1;
          b = Scan(hz, at, mode.ScanMs / 2, width);
          return (r, g, b, null, true);

        case SstvExtraLayout.Robot24:
          r = Scan(hz, at, mode.ScanMs, width); at += mode.ScanMs + 4;
          g = Scan(hz, at, mode.ScanMs / 2, width); at += mode.ScanMs / 2 + 4;
          b = Scan(hz, at, mode.ScanMs / 2, width);
          return (r, g, b, null, true);

        case SstvExtraLayout.RobotBw:
          r = Scan(hz, at, mode.ScanMs, width);
          return (r, r, r, null, false);

        case SstvExtraLayout.NarrowMc:
          r = Scan(hz, at, mode.ScanMs, width, true); at += mode.ScanMs;
          g = Scan(hz, at, mode.ScanMs, width, true); at += mode.ScanMs;
          b = Scan(hz, at, mode.ScanMs, width, true);
          return (r, g, b, null, false);

        case SstvExtraLayout.Avt:
          r = Scan(hz, 0, mode.ScanMs, width);
          g = Scan(hz, mode.ScanMs, mode.ScanMs, width);
          b = Scan(hz, 2 * mode.ScanMs, mode.ScanMs, width);
          return (r, g, b, null, false);

        default: throw new NotSupportedException(mode.Layout.ToString());
      }
    }

    private static double[] Scan(float[] frequencies,
      double atMs, double durationMs, int pixels, bool narrow = false)
    {
      double[] values = new double[pixels];
      double pixelMs = durationMs / pixels;
      for (int x = 0; x < pixels; x++)
      {
        // Narrow the integration window to reduce transitions' influence.
        // The analytic IIR path adds approximately half a millisecond.
        double beginMs = atMs + (x + .22) * pixelMs + .4;
        double endMs = atMs + (x + .78) * pixelMs + .4;
        int start = Math.Clamp((int)Math.Round(beginMs * 48), 0, frequencies.Length - 1);
        int end = Math.Clamp((int)Math.Round(endMs * 48), start + 1, frequencies.Length);
        double mean = 0;
        for (int i = start; i < end; i++) mean += frequencies[i];
        mean /= end - start;
        double v = narrow ? (mean - 2044) * 255 / 256 :
          (mean - SstvTones.Black) * 255 / SstvTones.Span;
        values[x] = Math.Clamp(v, 0, 255);
      }
      return values;
    }

    private static int Round(double value) => Math.Clamp(
      (int)Math.Round(value), 0, 255);
  }
}
