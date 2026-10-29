using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.AFC
{
  /// <summary>Read-only IF spectrum with explicit click-to-select target.</summary>
  public sealed class AfcSpectrumView : Control
  {
    private float[]? bins;
    private double minHz = -6000, stepHz = 12000.0 / 511;
    private double? observedHz;
    private double targetHz;
    public event Action<double>? TargetSelected;

    public AfcSpectrumView()
    {
      DoubleBuffered = true;
      ResizeRedraw = true;
      MinimumSize = new Size(170, 96);
      Cursor = Cursors.Cross;
      Font = new Font("Segoe UI", 9f);
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

    private double ToX(double freq, RectangleF plot)
    {
      double range = Math.Max(1.0, stepHz * Math.Max(1, (bins?.Length ?? 512) - 1));
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
      if (e.Button != MouseButtons.Left || bins is not { Length: > 8 })
        return;
      var plot = new RectangleF(38, 21, Math.Max(20, Width - 50),
        Math.Max(20, Height - 51));
      if (plot.Contains(e.Location))
        TargetSelected?.Invoke(Math.Round(ToHz(e.X, plot), 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(Color.FromArgb(13, 17, 23));
      var plot = new RectangleF(38, 21, Math.Max(20, Width - 50),
        Math.Max(20, Height - 51));
      using var gridPen = new Pen(Color.FromArgb(48, 61, 72));
      using var tracePen = new Pen(Color.FromArgb(88, 166, 255), 1.6f);
      using var targetPen = new Pen(Color.FromArgb(242, 204, 96), 1.4f)
        { DashStyle = DashStyle.Dash };
      using var observedPen = new Pen(Color.FromArgb(63, 185, 80), 1f);
      using var labelBrush = new SolidBrush(Color.FromArgb(172, 184, 197));
      using var signalBrush = new SolidBrush(Color.FromArgb(34, 88, 166, 255));
      g.DrawString("12 kHz IF · click a carrier to select its target", Font,
        labelBrush, new PointF(10, 2));
      for (int i = 0; i <= 4; i++)
      {
        float y = plot.Top + i * plot.Height / 4;
        g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
      }
      for (int i = 0; i <= 4; i++)
      {
        float x = plot.Left + i * plot.Width / 4;
        g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
        double hz = minHz + stepHz * Math.Max(1, (bins?.Length ?? 512) - 1) * i / 4.0;
        g.DrawString(hz.ToString("+0;-0;0") + " Hz", Font, labelBrush,
          x - 24, plot.Bottom + 3);
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
        using var area = new GraphicsPath();
        area.AddLines(points);
        area.AddLine(points[^1].X, plot.Bottom);
        area.AddLine(points[0].X, plot.Bottom);
        area.CloseFigure();
        g.FillPath(signalBrush, area);
        if (points.Length > 1) g.DrawLines(tracePen, points);
      }
      float targetX = (float)ToX(targetHz, plot);
      if (targetX >= plot.Left && targetX <= plot.Right)
        g.DrawLine(targetPen, targetX, plot.Top, targetX, plot.Bottom);
      if (observedHz is double hzObserved && double.IsFinite(hzObserved))
      {
        float x = (float)ToX(hzObserved, plot);
        if (x >= plot.Left && x <= plot.Right)
          g.DrawLine(observedPen, x, plot.Top, x, plot.Bottom);
      }
      g.DrawRectangle(gridPen, plot.Left, plot.Top, plot.Width, plot.Height);
    }
  }
}
