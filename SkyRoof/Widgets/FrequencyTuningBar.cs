using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Frequency ruler used by the dockable Frequency Control panel.
  /// Mouse dragging is previewed locally on every move while tuning updates are throttled,
  /// so CAT/SDR traffic cannot stall the UI at raw mouse-event rate.
  /// </summary>
  internal sealed class FrequencyTuningBar : Control
  {
    private int LastDragX;
    private double frequency;
    private double displayFrequency;
    private int PendingDragDelta;
    private bool Dragging;
    private const double SpanHz = 50000d;
    private readonly System.Windows.Forms.Timer DragCommitTimer = new() { Interval = 40 };

    public event Action<int>? TuneDeltaRequested;

    public FrequencyTuningBar()
    {
      DoubleBuffered = true;
      BackColor = Color.Black;
      ForeColor = Color.Aqua;
      Cursor = Cursors.SizeWE;
      SetStyle(ControlStyles.Selectable, true);
      TabStop = true;

      DragCommitTimer.Tick += (_, _) => FlushPendingDragDelta();
    }

    public void SetFrequency(double value)
    {
      frequency = value;

      // During a drag the ruler follows the local preview continuously. Model/CAT refreshes
      // must not snap the scale back between throttled commits.
      if (Dragging) return;

      if (Math.Abs(displayFrequency - value) < 0.5) return;
      displayFrequency = value;
      Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing) DragCommitTimer.Dispose();
      base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      if (Width < 2 || Height < 2) return;

      var g = e.Graphics;
      g.Clear(BackColor);

      double hzPerPixel = SpanHz / Math.Max(1, Width);
      double left = displayFrequency - SpanHz / 2d;
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
      Dragging = true;
      LastDragX = e.X;
      displayFrequency = frequency;
      PendingDragDelta = 0;
      DragCommitTimer.Start();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
      base.OnMouseMove(e);
      if (!Dragging || !Capture || e.Button != MouseButtons.Left || Width <= 0) return;

      int dx = e.X - LastDragX;
      if (dx == 0) return;

      int delta = (int)Math.Round(-dx * SpanHz / Width);
      if (delta == 0) return;

      LastDragX = e.X;
      PendingDragDelta += delta;
      displayFrequency += delta;
      Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
      base.OnMouseUp(e);
      if (e.Button != MouseButtons.Left) return;

      Capture = false;
      Dragging = false;
      DragCommitTimer.Stop();
      FlushPendingDragDelta();

      // The synchronous commit above normally refreshes frequency. This assignment also makes
      // the final position deterministic if no subscriber is present.
      displayFrequency = frequency;
      Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
      base.OnMouseCaptureChanged(e);
      if (Capture || !Dragging) return;

      Dragging = false;
      DragCommitTimer.Stop();
      FlushPendingDragDelta();
      displayFrequency = frequency;
      Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      int step = ModifierKeys.HasFlag(Keys.Alt) ? 500 : 20;
      TuneDeltaRequested?.Invoke(e.Delta > 0 ? step : -step);
    }

    private void FlushPendingDragDelta()
    {
      if (PendingDragDelta == 0) return;

      int delta = PendingDragDelta;
      PendingDragDelta = 0;
      TuneDeltaRequested?.Invoke(delta);
    }
  }
}
