using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using NAudio.Wave;
using VE3NEA.SkySSTV;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Offline, receive-safe SSTV audio WAV export for the Robot and PD modes
  /// available in SkySignals. Never keys PTT or chooses a CAT frequency.
  /// Phase, segment timing and 8-bit VIS follow the same shared mode table
  /// as the existing SkySignals decoder. Streaming avoids retaining several
  /// minutes of PCM or IQ in memory.
  /// </summary>
  public static class SstvWaveExporter
  {
    public const int SampleRate = 48000;
    public const double PeakLevel = 0.75;
    private const double TwoPi = 2 * Math.PI;

    public static void Export(Bitmap source, SstvMode mode, string outputPath)
    {
      ArgumentNullException.ThrowIfNull(source);
      if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path required", nameof(outputPath));
      SstvModeSpec spec = SstvModes.Get(mode);
      if (source.Width <= 0 || source.Height <= 0) throw new ArgumentException("Empty image", nameof(source));

      // Bitmap conversion in one managed-GDI step; FromBitmap reads 32-bit
      // BGRA and deliberately ignores alpha, which is flattened over white.
      using var rgbBitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
      using (var graphics = Graphics.FromImage(rgbBitmap))
      {
        graphics.Clear(Color.White);
        graphics.DrawImage(source, 0, 0, source.Width, source.Height);
      }
      var image = RgbImage.FromBitmap(rgbBitmap);

      string fullPath = Path.GetFullPath(outputPath);
      Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
      using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
      using var wave = new WaveFileWriter(stream, new WaveFormat(SampleRate, 16, 1));
      using var oscillator = new WaveOscillator(wave);
      EmitVis(oscillator, spec.VisCode);

      for (int line = 0; line < spec.LineCount; line++)
      {
        oscillator.Tone(SstvTones.Sync, spec.SyncMs);
        oscillator.Tone(SstvTones.Black, spec.SyncPorchMs);
        if (spec.Layout == SstvColorLayout.Robot36)
        {
          int y = line;
          oscillator.Scan(Component(image, spec, y, 0), spec.ScanYMs);
          bool red = (line & 1) == 0;
          oscillator.Tone(red ? SstvTones.Black : SstvTones.White, spec.SepMs);
          oscillator.Tone(SstvTones.Center, spec.SepPorchMs);
          oscillator.Scan(Component(image, spec, y, red ? 1 : 2), spec.ScanChromaMs);
        }
        else if (spec.Layout == SstvColorLayout.Robot72)
        {
          oscillator.Scan(Component(image, spec, line, 0), spec.ScanYMs);
          oscillator.Tone(SstvTones.Black, spec.SepMs);
          oscillator.Tone(SstvTones.Center, spec.SepPorchMs);
          oscillator.Scan(Component(image, spec, line, 1), spec.ScanChromaMs);
          oscillator.Tone(SstvTones.White, spec.SepMs);
          oscillator.Tone(SstvTones.Center, spec.SepPorchMs);
          oscillator.Scan(Component(image, spec, line, 2), spec.ScanChromaMs);
        }
        else if (spec.Layout == SstvColorLayout.Pd)
        {
          int y = 2 * line;
          oscillator.Scan(Component(image, spec, y, 0), spec.ScanYMs);
          oscillator.Scan(ComponentPair(image, spec, y, 1), spec.ScanChromaMs);
          oscillator.Scan(ComponentPair(image, spec, y, 2), spec.ScanChromaMs);
          oscillator.Scan(Component(image, spec, y + 1, 0), spec.ScanYMs);
        }
        else
          throw new NotSupportedException($"SSTV layout {spec.Layout} is not available for WAV export");
      }
      oscillator.Flush();
    }

    private static double[] Component(RgbImage image, SstvModeSpec spec, int row, int component)
    {
      var result = new double[spec.Width];
      int y = Math.Clamp((int)((long)row * image.Height / spec.Height), 0, image.Height - 1);
      for (int x = 0; x < result.Length; x++)
      {
        int xx = Math.Clamp((int)((long)x * image.Width / spec.Width), 0, image.Width - 1);
        var (r, g, b) = image.Get(xx, y);
        var (luma, cr, cb) = YCrCb.FromRgb(r, g, b);
        result[x] = component == 0 ? luma : component == 1 ? cr : cb;
      }
      return result;
    }

    private static double[] ComponentPair(RgbImage img, SstvModeSpec spec, int row, int channel)
    {
      double[] first = Component(img, spec, row, channel);
      double[] second = Component(img, spec, row + 1, channel);
      for (int x = 0; x < first.Length; x++) first[x] = 0.5 * (first[x] + second[x]);
      return first;
    }

    internal static void EmitVis(WaveOscillator writer, int visCode)
    {
      writer.Tone(SstvTones.Center, SstvTones.VisLeaderMs);
      writer.Tone(SstvTones.Sync, SstvTones.VisBreakMs);
      writer.Tone(SstvTones.Center, SstvTones.VisLeaderMs);
      writer.Tone(SstvTones.VisStartStop, SstvTones.VisBitMs);
      int parity = 0;
      for (int bit = 0; bit < 7; bit++)
      {
        int data = (visCode >> bit) & 1;
        parity ^= data;
        writer.Tone(data == 1 ? SstvTones.VisBitOne : SstvTones.VisBitZero, SstvTones.VisBitMs);
      }
      writer.Tone(parity == 1 ? SstvTones.VisBitOne : SstvTones.VisBitZero, SstvTones.VisBitMs);
      writer.Tone(SstvTones.VisStartStop, SstvTones.VisBitMs);
    }

    /// <summary>Continuous phase oscillator, 16-bit PCM with bounded blocks.</summary>
    internal sealed class WaveOscillator : IDisposable
    {
      private readonly WaveFileWriter output;
      private readonly byte[] bytes = new byte[8192];
      private int pending;
      private long sampleCount;
      private double idealSamples, phase;

      internal WaveOscillator(WaveFileWriter output) => this.output = output;

      internal void Tone(double frequency, double milliseconds)
      {
        int n = Samples(milliseconds);
        for (int i = 0; i < n; i++) WriteSample(frequency);
      }

      internal void Scan(double[] values, double milliseconds)
      {
        if (values.Length == 0) throw new ArgumentException("Empty scan", nameof(values));
        int n = Samples(milliseconds);
        for (int i = 0; i < n; i++)
        {
          int index = (int)((long)i * values.Length / Math.Max(1, n));
          WriteSample(SstvTones.ValueToFreq(values[Math.Min(values.Length - 1, index)]));
        }
      }

      private int Samples(double milliseconds)
      {
        idealSamples += SampleRate * milliseconds / 1000.0;
        return Math.Max(0, checked((int)(Math.Round(idealSamples) - sampleCount)));
      }

      private void WriteSample(double frequency)
      {
        phase += TwoPi * frequency / SampleRate;
        if (phase >= TwoPi) phase -= TwoPi;
        short pcm = (short)Math.Round(Math.Sin(phase) * (short.MaxValue * PeakLevel));
        bytes[pending++] = (byte)pcm;
        bytes[pending++] = (byte)(pcm >> 8);
        sampleCount++;
        if (pending == bytes.Length) Flush();
      }

      internal void Flush()
      {
        if (pending == 0) return;
        output.Write(bytes, 0, pending);
        pending = 0;
      }

      public void Dispose() => Flush();
    }
  }
}
