using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.AFC
{
  /// <summary>Measured relative-dB IF FFT, never a placeholder curve.</summary>
  public sealed class AfcSpectrumView : Control
  {
    private float[]? bins;
    private double minHz = -6000, stepHz = 12000.0 / 511;
    private double? observedHz;
    private double targetHz;
    private bool canSelect = true;
    private AfcThemePalette palette = AfcThemePalette.Current;
    public event Action<double>? TargetSelected;

    public AfcSpectrumView()
    {
      DoubleBuffered = true;
      ResizeRedraw = true;
      MinimumSize = new Size(180, 105);
      Cursor = Cursors.Cross;
      Font = new Font("Segoe UI", 9f);
      BackColor = palette.Plot;
      ForeColor = palette.Text;
    }

    public void ApplyPalette(AfcThemePalette colors)
    {
      palette = colors;
      BackColor = colors.Plot;
      ForeColor = colors.Text;
      Invalidate();
    }

    public void SetTargetInteraction(bool allow)
    {
      canSelect = allow;
      Cursor = allow ? Cursors.Cross : Cursors.Default;
      Invalidate();
    }

    public void SetSpectrum(float[]? db, double min, double step,
      double? observed, double target)
    {
      bins = db;
      if (double.IsFinite(min) && double.IsFinite(step) && step > 0)
      {
        minHz = min;
        stepHz = step;
      }
      observedHz = observed;
      targetHz = target;
      Invalidate();
    }

    private RectangleF Plot => new(55, 31, Math.Max(20, Width - 70),
      Math.Max(20, Height - 73));

    private double ToX(double freq, RectangleF plot)
    {
      double range = Math.Max(1, stepHz * Math.Max(1, (bins?.Length ?? 512) - 1));
      return plot.Left + (freq - minHz) / range * plot.Width;
    }

    private double ToHz(double x, RectangleF plot)
    {
      double range = stepHz * Math.Max(1, (bins?.Length ?? 512) - 1);
      return minHz + Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * range;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
      base.OnMouseDown(e);
      if (!canSelect || e.Button != MouseButtons.Left ||
          bins is not { Length: > 8 }) return;
      var plot = Plot;
      if (plot.Contains(e.Location))
        TargetSelected?.Invoke(Math.Round(ToHz(e.X, plot), 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(palette.Plot);
      RectangleF plot = Plot;
      using var gridPen = new Pen(Color.FromArgb(palette.Dark ? 70 : 55, palette.Border));
      using var tracePen = new Pen(palette.Trace, 1.5f);
      using var centerPen = new Pen(palette.Warning, 1.4f)
        { DashStyle = DashStyle.Dash };
      using var observedPen = new Pen(palette.Good, 1.25f);
      using var labelBrush = new SolidBrush(palette.Muted);
      using var titleBrush = new SolidBrush(palette.Text);
      using var fillBrush = new SolidBrush(Color.FromArgb(palette.Dark ? 42 : 36, palette.Trace));

      g.DrawString("12 kHz IF  ·  实时 FFT 频谱", Font,
        titleBrush, new PointF(plot.Left, 8));
      if (canSelect)
      {
        string hint = "点击峰值选择 CW / 导频";
        SizeF size = g.MeasureString(hint, Font);
        if (plot.Width > size.Width + 175)
          g.DrawString(hint, Font, labelBrush,
            new PointF(plot.Right - size.Width, 8));
      }

      for (int i = 0; i <= 4; i++)
      {
        float y = plot.Top + i * plot.Height / 4;
        g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        g.DrawString((-70 + 70 * (4 - i) / 4).ToString() + " dB",
          Font, labelBrush, 3, y - 8);
      }
      for (int i = 0; i <= 4; i++)
      {
        float x = plot.Left + i * plot.Width / 4;
        g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
        double hz = minHz + stepHz * Math.Max(1, (bins?.Length ?? 512) - 1) * i / 4.0;
        g.DrawString(hz.ToString("+0;-0;0"), Font, labelBrush,
          x - 23, plot.Bottom + 5);
      }
      if (bins is { Length: > 8 } values)
      {
        var points = new PointF[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
          double db = Math.Clamp(double.IsFinite(values[i]) ? values[i] : -70, -70, 0);
          points[i] = new PointF(
            plot.Left + i * plot.Width / Math.Max(1, values.Length - 1),
            plot.Bottom - (float)((db + 70) / 70 * plot.Height));
        }
        using var region = new GraphicsPath();
        region.AddLines(points);
        region.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
        region.AddLine(points[^1].X, plot.Bottom, points[0].X, plot.Bottom);
        region.CloseFigure();
        g.FillPath(fillBrush, region);
        g.DrawLines(tracePen, points);
      }
      // Orange is always the fixed 12-kHz IF reference (offset 0 Hz).
      float referenceX = (float)ToX(0, plot);
      if (referenceX >= plot.Left && referenceX <= plot.Right)
        g.DrawLine(centerPen, referenceX, plot.Top, referenceX, plot.Bottom);
      if (canSelect)
      {
        float selectedX = (float)ToX(targetHz, plot);
        if (selectedX >= plot.Left && selectedX <= plot.Right &&
            Math.Abs(selectedX - referenceX) >= 3)
          g.DrawLine(new Pen(palette.Accent, 1f), selectedX, plot.Top, selectedX, plot.Bottom);
      }
      if (observedHz is double hzObserved && double.IsFinite(hzObserved))
      {
        float x = (float)ToX(hzObserved, plot);
        if (x >= plot.Left && x <= plot.Right)
          g.DrawLine(observedPen, x, plot.Top, x, plot.Bottom);
      }
      g.DrawRectangle(gridPen, plot.Left, plot.Top, plot.Width, plot.Height);
      const string footer = "目标中心 / 12 kHz IF";
      SizeF foot = g.MeasureString(footer, Font);
      g.DrawString(footer, Font, titleBrush,
        plot.Left + (plot.Width - foot.Width) / 2, Height - foot.Height - 2);
    }
  }
}
