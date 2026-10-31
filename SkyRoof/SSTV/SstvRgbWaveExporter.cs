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
  /// Offline VIS+RGB SSTV waveform encoder for MMSSTV Scottie, Martin and
  /// SC2 modes. Timing/component order follows MMSSTV Main.cpp's LineSCT,
  /// LineMRT and LineSC2180 functions. It has no access to CAT or PTT.
  /// </summary>
  public static class SstvRgbWaveExporter
  {
    public static void Export(Bitmap source, SstvRgbTxSpec spec, string outputPath,
      CancellationToken cancellationToken = default)
    {
      ArgumentNullException.ThrowIfNull(source);
      ArgumentNullException.ThrowIfNull(spec);
      if (string.IsNullOrWhiteSpace(outputPath))
        throw new ArgumentException("Output filename is required.", nameof(outputPath));
      if (source.Width <= 0 || source.Height <= 0)
        throw new ArgumentException("Input image must be nonempty.", nameof(source));

      using var rgb = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
      using (var g = Graphics.FromImage(rgb))
      {
        g.Clear(Color.White);
        g.DrawImage(source, 0, 0, rgb.Width, rgb.Height);
      }
      RgbImage image = RgbImage.FromBitmap(rgb);
      string file = Path.GetFullPath(outputPath);
      Directory.CreateDirectory(Path.GetDirectoryName(file)!);
      using var stream = new FileStream(file, FileMode.Create, FileAccess.Write);
      using var writer = new WaveFileWriter(stream,
        new WaveFormat(SstvWaveExporter.SampleRate, 16, 1));
      using var osc = new SstvWaveExporter.WaveOscillator(writer, cancellationToken);
      SstvWaveExporter.EmitVis(osc, spec.VisCode7);

      // MMSSTV inserts this one-time Scottie 9-ms sync immediately after VIS.
      if (spec.Layout == SstvRgbTxLayout.Scottie)
        osc.Tone(SstvTones.Sync, 9.0);

      for (int y = 0; y < spec.Height; y++)
      {
        double[] red = Component(image, spec, y, 0);
        double[] green = Component(image, spec, y, 1);
        double[] blue = Component(image, spec, y, 2);
        if (spec.Layout == SstvRgbTxLayout.Scottie)
        {
          osc.Tone(SstvTones.Black, 1.5);
          osc.Scan(green, spec.ScanMs);
          osc.Tone(SstvTones.Black, 1.5);
          osc.Scan(blue, spec.ScanMs);
          osc.Tone(SstvTones.Sync, 9.0);
          osc.Tone(SstvTones.Black, 1.5);
          osc.Scan(red, spec.ScanMs);
        }
        else if (spec.Layout == SstvRgbTxLayout.Martin)
        {
          osc.Tone(SstvTones.Sync, spec.SyncMs);
          osc.Tone(SstvTones.Black, spec.PorchMs);
          osc.Scan(green, spec.ScanMs);
          osc.Tone(SstvTones.Black, spec.PorchMs);
          osc.Scan(blue, spec.ScanMs);
          osc.Tone(SstvTones.Black, spec.PorchMs);
          osc.Scan(red, spec.ScanMs);
          osc.Tone(SstvTones.Black, spec.PorchMs);
        }
        else if (spec.Layout == SstvRgbTxLayout.Sc2)
        {
          osc.Tone(SstvTones.Sync, spec.SyncMs);
          osc.Tone(SstvTones.Black, spec.PorchMs);
          osc.Scan(red, spec.ScanMs);
          osc.Scan(green, spec.ScanMs);
          osc.Scan(blue, spec.ScanMs);
        }
        else throw new InvalidOperationException("Unknown SSTV RGB transmit layout.");
      }
      osc.Flush();
    }

    private static double[] Component(RgbImage img,
      SstvRgbTxSpec mode, int row, int channel)
    {
      var result = new double[mode.Width];
      int y = Math.Clamp((int)((long)row * img.Height / mode.Height), 0, img.Height - 1);
      for (int x = 0; x < mode.Width; x++)
      {
        int xx = Math.Clamp((int)((long)x * img.Width / mode.Width), 0, img.Width - 1);
        var (r, g, b) = img.Get(xx, y);
        result[x] = channel == 0 ? r : channel == 1 ? g : b;
      }
      return result;
    }

    public static void Export(Bitmap source, SstvTxModeProfile mode, string outputPath,
      CancellationToken cancellationToken = default)
    {
      if (mode.Native is SstvMode standard)
        SstvWaveExporter.Export(source, standard, outputPath, cancellationToken);
      else if (mode.Rgb is { } rgb)
        Export(source, rgb, outputPath, cancellationToken);
      else if (mode.Extra is { } extra)
        SstvExtraWaveExporter.Export(source, extra, outputPath, cancellationToken);
      else
        throw new NotSupportedException("SSTV transmit mode not supported.");
    }
  }
}
