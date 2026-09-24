using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Compact HRD-style frequency ruler. It is intentionally an input/view only control:
  /// all tuning semantics remain in RadioLink / FrequencyWidget.
  /// </summary>
  internal sealed class FrequencyTuningBar : Control
  {
    private int LastDragX;
    private double frequency;
    private const double SpanHz = 50000d;

    public event Action<int>? TuneDeltaRequested;

    public void SetFrequency(double value)
    {
      if (Math.Abs(frequency - value) < 0.5) return;
      frequency = value;
      Invalidate();
    }

    public FrequencyTuningBar()
    {
      DoubleBuffered = true;
      BackColor = Color.Black;
      ForeColor = Color.Aqua;
      Cursor = Cursors.SizeWE;
      SetStyle(ControlStyles.Selectable, true);
      TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      if (Width < 2 || Height < 2) return;

      var g = e.Graphics;
      g.Clear(BackColor);

      double hzPerPixel = SpanHz / Math.Max(1, Width);
      double left = frequency - SpanHz / 2d;
      const int majorStep = 10000;
      const int minorStep = 1000;

      long firstMinor = (long)Math.Ceiling(left / minorStep) * minorStep;
      using var minorPen = new Pen(Color.DimGray);
      using var majorPen = new Pen(Color.Gray);
      using var centerPen = new Pen(Color.Lime, 2);
      using var font = new Font("Segoe UI", 7F);
      using var brush = new SolidBrush(ForeColor);

      for (long f = firstMinor; f <= left + SpanHz; f += minorStep)
      {
        int x = (int)Math.Round((f - left) / hzPerPixel);
        bool major = f % majorStep == 0;
        int tickTop = major ? 1 : Height / 2;
        g.DrawLine(major ? majorPen : minorPen, x, tickTop, x, Height - 1);

        if (major && Height >= 22)
        {
          string label = (f / 1000d).ToString("N0");
          var size = g.MeasureString(label, font);
          g.DrawString(label, font, brush, x - size.Width / 2, 1);
        }
      }

      int center = Width / 2;
      g.DrawLine(centerPen, center, 0, center, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
      base.OnMouseDown(e);
      if (e.Button != MouseButtons.Left) return;
      Focus();
      Capture = true;
      LastDragX = e.X;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
      base.OnMouseMove(e);
      if (!Capture || e.Button != MouseButtons.Left || Width <= 0) return;

      int dx = e.X - LastDragX;
      if (dx == 0) return;

      int delta = (int)Math.Round(-dx * SpanHz / Width);
      if (delta == 0) return;

      LastDragX = e.X;
      TuneDeltaRequested?.Invoke(delta);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
      base.OnMouseUp(e);
      if (e.Button == MouseButtons.Left) Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      int step = ModifierKeys.HasFlag(Keys.Alt) ? 500 : 20;
      TuneDeltaRequested?.Invoke(e.Delta > 0 ? step : -step);
    }
  }
}
