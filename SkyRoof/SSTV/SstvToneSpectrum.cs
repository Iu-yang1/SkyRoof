using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// Lightweight, bounded 48-kHz AF spectrum for the MMSSTV-style receive
  /// tuning strip. No hardware/CAT writes and no native FFT dependencies.
  /// </summary>
  public static class SstvToneSpectrum
  {
    public const int MinHz = 900;
    public const int MaxHz = 2400;
    public const int BinStepHz = 25;
    public const int BinCount = (MaxHz - MinHz) / BinStepHz + 1;
    private const int SampleRate = 48000;
    private const int WindowSamples = 2048;

    public static float[] Measure(ReadOnlySpan<float> samples)
    {
      var levels = new float[BinCount];
      if (samples.Length < WindowSamples) return levels;
      ReadOnlySpan<float> audio = samples.Slice(samples.Length - WindowSamples);
      for (int b = 0; b < levels.Length; b++)
      {
        double freq = MinHz + b * BinStepHz;
        double omega = 2 * Math.PI * freq / SampleRate;
        double coeff = 2 * Math.Cos(omega);
        double prev = 0, prev2 = 0;
        for (int n = 0; n < WindowSamples; n++)
        {
          if (!float.IsFinite(audio[n])) return new float[BinCount];
          double taper = .5 - .5 * Math.Cos(2 * Math.PI * n / (WindowSamples - 1));
          double value = audio[n] * taper + coeff * prev - prev2;
          prev2 = prev;
          prev = value;
        }
        double power = Math.Max(0, prev2 * prev2 + prev * prev - coeff * prev * prev2);
        // Scale to 0..1 relative to a full-scale sine with Hann window.
        double amplitude = 4.0 * Math.Sqrt(power) / WindowSamples;
        levels[b] = (float)Math.Clamp(amplitude, 0, 1);
      }
      return levels;
    }
  }

  /// <summary>
  /// Read-only operator display: the visible 1200-Hz sync, 1500-Hz black,
  /// 1900-Hz leader and 2300-Hz white markers match SSTV on-air tuning.
  /// </summary>
  public sealed class SstvToneSpectrumView : Control
  {
    private float[] bins = new float[SstvToneSpectrum.BinCount];

    public SstvToneSpectrumView()
    {
      DoubleBuffered = true;
      Height = 106;
      MinimumSize = new Size(240, 90);
      BackColor = Color.FromArgb(23, 27, 34);
      ForeColor = Color.FromArgb(222, 231, 243);
    }

    public void SetLevels(float[] levels)
    {
      if (levels == null || levels.Length != SstvToneSpectrum.BinCount)
        throw new ArgumentException("Incorrect SSTV spectrum bins", nameof(levels));
      bins = (float[])levels.Clone();
      Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      int left = 10, right = Width - 12, top = 8, bottom = Height - 23;
      if (right <= left || bottom <= top) return;
      int w = right - left, h = bottom - top;
      using var grid = new Pen(Color.FromArgb(56, 65, 78));
      using var signal = new Pen(Color.FromArgb(71, 189, 241), 2f);
      using var labelFont = new Font(FontFamily.GenericSansSerif, 8f);
      using var labels = new SolidBrush(ForeColor);
      g.DrawLine(grid, left, bottom, right, bottom);
      int[] ticks = { 1200, 1500, 1900, 2300 };
      string[] names = { "SYNC", "BLACK", "VIS", "WHITE" };
      for (int i = 0; i < ticks.Length; i++)
      {
        int x = left + (int)Math.Round((ticks[i] - SstvToneSpectrum.MinHz) *
          w / (double)(SstvToneSpectrum.MaxHz - SstvToneSpectrum.MinHz));
        using var marker = new Pen(i == 2 ?
          Color.FromArgb(100, 209, 163) : Color.FromArgb(84, 91, 105));
        g.DrawLine(marker, x, top, x, bottom);
        string text = names[i] + " " + ticks[i];
        SizeF textSize = g.MeasureString(text, labelFont);
        g.DrawString(text, labelFont, labels,
          Math.Clamp(x - textSize.Width / 2, left, right - textSize.Width),
          bottom + 3);
      }
      PointF[] trace = new PointF[bins.Length];
      for (int i = 0; i < bins.Length; i++)
      {
        int x = left + i * w / (bins.Length - 1);
        // The weak end remains visible without claiming SNR or dBm.
        double normalized = Math.Sqrt(Math.Clamp(bins[i], 0, 1));
        trace[i] = new PointF(x, bottom - (float)(normalized * (h - 3)));
      }
      if (trace.Length >= 2) g.DrawLines(signal, trace);
    }
  }
}
