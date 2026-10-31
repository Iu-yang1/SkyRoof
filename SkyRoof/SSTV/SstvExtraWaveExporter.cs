using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using NAudio.Wave;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Remaining MMSSTV-compatible transmit families. Source protocol:
  /// n5ac/mmsstv Main.cpp LineP/LineMP/LineMR/LineMN/LineMC/
  /// LineR24/LineRM/LineAVT and mode.txt 16-bit VIS and N-VIS.
  /// This module is TX *encoding* only. Receiver parity is separate.
  /// </summary>
  public static class SstvExtraWaveExporter
  {
    private const double NarrowBlack = 2044;
    private const double NarrowWhite = 2300;

    public static void Export(Bitmap source, SstvExtraTxSpec mode, string destination,
      CancellationToken cancellationToken = default,
      SstvIdOptions? stationId = null)
    {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(mode);
      if (source.Width == 0 || source.Height == 0)
        throw new ArgumentException("SSTV image has no pixels.", nameof(source));
      if (string.IsNullOrWhiteSpace(destination))
        throw new ArgumentException("Output WAV path is required.", nameof(destination));

      using var bgra = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(bgra))
      {
        g.Clear(Color.White);
        g.DrawImage(source, 0, 0, source.Width, source.Height);
      }
      RgbImage img = RgbImage.FromBitmap(bgra);
      string path = Path.GetFullPath(destination);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
      using var wav = new WaveFileWriter(stream,
        new WaveFormat(SstvWaveExporter.SampleRate, 16, 1));
      using var osc = new SstvWaveExporter.WaveOscillator(wav, cancellationToken);
      EmitHeader(osc, mode);

      for (int scan = 0; scan < mode.LineCount; scan++)
      {
        int y = scan * mode.RowsPerScan;
        switch (mode.Layout)
        {
          case SstvExtraLayout.P:
            osc.Tone(SstvTones.Sync, mode.SyncMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            osc.Scan(Component(img, mode, y, 0), mode.ScanMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            osc.Scan(Component(img, mode, y, 1), mode.ScanMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            osc.Scan(Component(img, mode, y, 2), mode.ScanMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            break;

          case SstvExtraLayout.Mp:
          case SstvExtraLayout.NarrowMp:
            bool narrowMp = mode.Narrow;
            double black = narrowMp ? NarrowBlack : SstvTones.Black;
            double sync = narrowMp ? 1900 : SstvTones.Sync;
            osc.Tone(sync, mode.SyncMs);
            osc.Tone(black, mode.PorchMs);
            osc.ScanValues(Component(img, mode, y, 3), mode.ScanMs,
              black, NarrowWhite);
            double[] cr = Component(img, mode, y, 4);
            double[] cb = Component(img, mode, y, 5);
            osc.ScanValues(cr, mode.ScanMs, black, NarrowWhite);
            osc.ScanValues(cb, mode.ScanMs, black, NarrowWhite);
            osc.ScanValues(Component(img, mode, y + 1, 3), mode.ScanMs,
              black, NarrowWhite);
            break;

          case SstvExtraLayout.Mr:
            osc.Tone(SstvTones.Sync, mode.SyncMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            double[] luma = Component(img, mode, y, 3);
            double[] mrCr = Component(img, mode, y, 4);
            double[] mrCb = Component(img, mode, y, 5);
            osc.Scan(luma, mode.ScanMs);
            osc.Tone(SstvTones.ValueToFreq(luma[^1]), .1);
            osc.Scan(mrCr, mode.ScanMs / 2);
            osc.Tone(SstvTones.ValueToFreq(mrCr[^1]), .1);
            osc.Scan(mrCb, mode.ScanMs / 2);
            osc.Tone(SstvTones.ValueToFreq(mrCb[^1]), .1);
            break;

          case SstvExtraLayout.NarrowMc:
            osc.Tone(1900, mode.SyncMs);
            osc.Tone(NarrowBlack, mode.PorchMs);
            osc.ScanValues(Component(img, mode, y, 0), mode.ScanMs,
              NarrowBlack, NarrowWhite);
            osc.ScanValues(Component(img, mode, y, 1), mode.ScanMs,
              NarrowBlack, NarrowWhite);
            osc.ScanValues(Component(img, mode, y, 2), mode.ScanMs,
              NarrowBlack, NarrowWhite);
            break;

          case SstvExtraLayout.RobotBw:
            osc.Tone(SstvTones.Sync, mode.SyncMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            double[] mono1 = Component(img, mode, y, 3);
            double[] mono2 = Component(img, mode, y + 1, 3);
            for (int x = 0; x < mono1.Length; x++)
              mono1[x] = (mono1[x] + mono2[x]) / 2;
            osc.Scan(mono1, mode.ScanMs);
            break;

          case SstvExtraLayout.Robot24:
            osc.Tone(SstvTones.Sync, mode.SyncMs);
            osc.Tone(SstvTones.Black, mode.PorchMs);
            osc.Scan(Component(img, mode, y, 3), mode.ScanMs);
            osc.Tone(SstvTones.Black, 3);
            osc.Tone(SstvTones.Center, 1);
            osc.Scan(Component(img, mode, y, 4), mode.ScanMs / 2);
            osc.Tone(SstvTones.White, 3);
            osc.Tone(SstvTones.Center, 1);
            osc.Scan(Component(img, mode, y, 5), mode.ScanMs / 2);
            break;

          case SstvExtraLayout.Avt:
            osc.Scan(Component(img, mode, y, 0), mode.ScanMs);
            osc.Scan(Component(img, mode, y, 1), mode.ScanMs);
            osc.Scan(Component(img, mode, y, 2), mode.ScanMs);
            break;

          default: throw new NotSupportedException(mode.Layout.ToString());
        }
      }
      SstvTxIdentifier.Append(osc, stationId);
      osc.Flush();
    }

    private static double[] Component(RgbImage image,
      SstvExtraTxSpec mode, int row, int channel)
    {
      int y = Math.Clamp((int)((long)row * image.Height / mode.Height),
        0, image.Height - 1);
      double[] values = new double[mode.Width];
      for (int x = 0; x < mode.Width; x++)
      {
        int column = Math.Clamp((int)((long)x * image.Width / mode.Width),
          0, image.Width - 1);
        var (r, g, b) = image.Get(column, y);
        if (channel < 3)
          values[x] = channel switch { 0 => r, 1 => g, _ => b };
        else
        {
          var (luma, cr, cb) = YCrCb.FromRgb(r, g, b);
          values[x] = channel switch { 3 => luma, 4 => cr, _ => cb };
        }
      }
      return values;
    }

    internal static void EmitHeader(SstvWaveExporter.WaveOscillator osc,
      SstvExtraTxSpec spec)
    {
      if (spec.Narrow)
      {
        // Mode.txt 24-bit N-VIS: 101101, 010101, code, code xor 010101
        osc.Tone(1900, 300);
        osc.Tone(2100, 100);
        osc.Tone(1900, 22);
        foreach (int group in new[] { 0x2D, 0x15, spec.VisCode, spec.VisCode ^ 0x15 })
          for (int bit = 5; bit >= 0; bit--)
            osc.Tone((group & (1 << bit)) != 0 ? 1900 : 2100, 22);
      }
      else
      {
        int repeats = spec.Layout == SstvExtraLayout.Avt ? 3 : 1;
        for (int i = 0; i < repeats; i++)
        {
          if (spec.Extended)
          {
            osc.Tone(1900, 300);
            osc.Tone(1200, 10);
            osc.Tone(1900, 300);
            osc.Tone(1200, 30);
            for (int bit = 0; bit < 16; bit++)
              osc.Tone((spec.VisCode & (1 << bit)) != 0 ? 1100 : 1300, 30);
            osc.Tone(1200, 30);
          }
          else
            SstvWaveExporter.EmitVis(osc, spec.VisCode);
        }
        if (spec.Layout == SstvExtraLayout.Avt)
        {
          int pattern = 0x5FA0;
          for (int row = 0; row < 32; row++)
          {
            osc.Tone(1900, 9.7646);
            int data = pattern;
            for (int bit = 0; bit < 16; bit++)
            {
              osc.Tone((data & 0x8000) != 0 ? 1600 : 2200, 9.7646);
              data <<= 1;
            }
            pattern = ((pattern & 0xFF00) - 0x0100) | ((pattern & 0x00FF) + 1);
          }
          osc.Tone(1900, .30514375);
        }
      }
    }
  }
}
