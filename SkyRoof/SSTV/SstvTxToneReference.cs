using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// MMSSTV-style SSTV AF frequency scale. This is a labelled frequency
  /// reference, not a synthetic/pretend live TX spectrum.
  /// </summary>
  public sealed class SstvTxToneReference : Control
  {
    private static readonly int[] Frequencies = { 1200, 1500, 1900, 2300 };

    public SstvTxToneReference()
    {
      DoubleBuffered = true;
      Height = 76;
      Dock = DockStyle.Top;
      BackColor = Color.FromArgb(24, 27, 32);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      var area = new Rectangle(7, 23, Math.Max(1, Width - 14),
        Math.Max(1, Height - 28));
      using (var brush = new LinearGradientBrush(area,
        Color.FromArgb(245, 130, 57), Color.FromArgb(250, 94, 35),
        LinearGradientMode.Vertical))
        g.FillRectangle(brush, area);
      using var border = new Pen(Color.FromArgb(85, 90, 100));
      g.DrawRectangle(border, area);

      using var title = new SolidBrush(Color.Gainsboro);
      using var tick = new Pen(Color.FromArgb(218, 253, 202), 1);
      using var font = new Font(Font.FontFamily, 8f, FontStyle.Regular);
      g.DrawString("TX AF 音调参考", font, title, 7, 3);
      for (int i = 0; i < Frequencies.Length; i++)
      {
        float x = area.Left + (Frequencies[i] - 1000f) /
          1500f * area.Width;
        g.DrawLine(tick, x, area.Top + 2, x, area.Bottom - 2);
        string label = Frequencies[i].ToString();
        SizeF size = g.MeasureString(label, font);
        g.DrawString(label, font, title,
          Math.Clamp(x - size.Width / 2, 0, Math.Max(0, Width - size.Width)),
          3);
      }
    }
  }
}
