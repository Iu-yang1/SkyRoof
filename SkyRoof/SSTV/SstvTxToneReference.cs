using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// A shared MMSSTV-style audio tone reference for TX and RX AF/IF.
  /// Both pages use the exact same painter and four numeric scale labels.
  /// The orange area is a reference scale; the black strip is unlabelled.
  /// </summary>
  public sealed class SstvTxToneReference : Control
  {
    public static readonly int[] ReferenceFrequencies = { 1200, 1500, 1900, 2300 };
    private float[]? spectrumLevels;

    // RX may render the incoming AF/IF spectrum in the otherwise black
    // lower strip; TX leaves that strip blank. The reference geometry and
    // numeric markers stay identical in both views.
    public void SetLevels(float[] levels)
    {
      if (levels == null || levels.Length != SstvToneSpectrum.BinCount)
        throw new ArgumentException("Invalid SSTV spectrum length", nameof(levels));
      spectrumLevels = (float[])levels.Clone();
      Invalidate();
    }

    public SstvTxToneReference()
    {
      DoubleBuffered = true;
      Height = 90;
      Dock = DockStyle.Top;
      BackColor = SystemColors.Control;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.None;
      int left = 5, right = Math.Max(6, ClientSize.Width - 5);
      int width = Math.Max(1, right - left);
      const int labelHeight = 18;
      int blackHeight = Math.Max(18, ClientSize.Height / 3);
      int orangeBottom = Math.Max(labelHeight + 2, ClientSize.Height - blackHeight - 4);
      var orange = new Rectangle(left, labelHeight,
        width, Math.Max(1, orangeBottom - labelHeight));
      var black = new Rectangle(left, orange.Bottom + 4,
        width, Math.Max(1, ClientSize.Height - orange.Bottom - 7));

      using var warm = new SolidBrush(Color.FromArgb(255, 125, 58));
      using var blackBrush = new SolidBrush(Color.Black);
      using var border = new Pen(Color.FromArgb(116, 119, 124));
      using var highlight = new Pen(Color.FromArgb(144, 132, 228));
      using var marker = new Pen(Color.FromArgb(146, 238, 150));
      using var numberBrush = new SolidBrush(Color.FromArgb(45, 45, 45));
      using var font = new Font(Font.FontFamily, 8.25f, FontStyle.Regular);

      g.FillRectangle(warm, orange);
      g.DrawRectangle(border, orange);
      g.DrawLine(highlight, orange.Left, orange.Bottom + 1,
        orange.Right, orange.Bottom + 1);
      g.FillRectangle(blackBrush, black);
      g.DrawRectangle(border, black);
      if (spectrumLevels is { Length: > 1 } bins)
      {
        using var livePen = new Pen(Color.FromArgb(68, 192, 241), 1.5f);
        var points = new PointF[bins.Length];
        for (int i = 0; i < bins.Length; i++)
        {
          float x = black.Left + i * black.Width / (float)(bins.Length - 1);
          float strength = MathF.Sqrt(Math.Clamp(bins[i], 0, 1));
          points[i] = new PointF(x, black.Bottom - 2 -
            strength * Math.Max(1, black.Height - 4));
        }
        g.DrawLines(livePen, points);
      }

      foreach (int frequency in ReferenceFrequencies)
      {
        float x = orange.Left + (frequency - 1000f) / 1500f * orange.Width;
        g.DrawLine(marker, x, orange.Top + 1, x, orange.Bottom - 1);
        string value = frequency.ToString();
        SizeF size = g.MeasureString(value, font);
        g.DrawString(value, font, numberBrush,
          Math.Clamp(x - size.Width / 2, 0, Math.Max(0, Width - size.Width)), 0);
      }
    }
  }
}
