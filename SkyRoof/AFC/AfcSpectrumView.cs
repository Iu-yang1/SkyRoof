using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SkyRoof.AFC
{
  /// <summary>
  /// Theme-aware trace + optional scroll waterfall for measured real IF PCM.
  /// One source FFT bin is never simply discarded to resize a trace: every
  /// pixel retains the strongest bin in its covered frequency interval.
  /// The waterfall bitmap is a lockbits-updated circular history.
  /// </summary>
  public sealed class AfcSpectrumView : Control
  {
    private const int HistoryRows = 180;
    private const int HistoryWidth = 1024;
    // 60-dB visual span around the REF LEVEL: +15 dB by default.
    // This display-only offset never touches the AFC error or DSP loop.
    private const float VerticalRangeDb = 60f;
    private const double DefaultReferenceDb = 15;
    private const double ReferenceMinDb = -40;
    private const double ReferenceMaxDb = 60;
    private double referenceLevelDb = DefaultReferenceDb;
    private static readonly int[] RsBa1WaterfallPalette =
      IcomLanSpectrumView.BuildPalette(IcomScopeWaterfallPalette.Classic);
    private static readonly Color RsBa1Trace = Color.FromArgb(44, 219, 88);
    private static readonly Color RsBa1Peak = Color.FromArgb(255, 203, 74);
    private static readonly Color ScopeText = Color.FromArgb(225, 236, 248);
    private static readonly Color ScopeGrid = Color.FromArgb(59, 75, 95);
    private Color SpectrumBackground => palette.Dark
      ? Color.FromArgb(5, 10, 27) : Color.FromArgb(9, 22, 45);
    private float DisplayTop => (float)(referenceLevelDb + 30);
    private float DisplayBottom => (float)(referenceLevelDb - 30);
    private float[]? bins, heldPeaks;
    private double minHz = -6000, stepHz = 12000.0 / 511;
    private double? observedHz;
    private double targetHz;
    private bool canSelect = true;
    private bool showWaterfall = true, peakHold;
    private int spanHz = 12000;
    private long generation = long.MinValue;
    private int historyHead = -1, historyCount;
    private readonly Bitmap history = new(
      HistoryWidth, HistoryRows, PixelFormat.Format32bppArgb);
    private readonly int[] linePixels = new int[HistoryWidth];
    private readonly float[][] historyLevels = new float[HistoryRows][];
    private AfcThemePalette palette = AfcThemePalette.Current;
    public event Action<double>? TargetSelected;
    public event Action<int>? SpanChanged;
    public int SpanHz => spanHz;
    public bool ShowWaterfall => showWaterfall;
    public bool PeakHold => peakHold;

    public AfcSpectrumView()
    {
      DoubleBuffered = true;
      ResizeRedraw = true;
      MinimumSize = new Size(180, 105);
      Cursor = Cursors.Cross;
      Font = new Font("Segoe UI", 9f);
      BackColor = SpectrumBackground;
      ForeColor = ScopeText;
    }

    internal void ApplyPalette(AfcThemePalette colors)
    {
      palette = colors;
      BackColor = SpectrumBackground;
      ForeColor = ScopeText;
      RebuildHistoryColors();
      Invalidate();
    }

    internal double ReferenceLevelDb => referenceLevelDb;

    public void SetReferenceLevel(double value)
    {
      // Reject invalid configuration values without corrupting the raster.
      if (!double.IsFinite(value)) value = DefaultReferenceDb;
      value = Math.Clamp(value, ReferenceMinDb, ReferenceMaxDb);
      if (Math.Abs(referenceLevelDb - value) < .01) return;
      referenceLevelDb = value;
      // Original dB rows are preserved so REF and theme changes re-color
      // the existing history without erasing the operator's waterfall.
      RebuildHistoryColors();
      Invalidate();
    }

    public void SetTargetInteraction(bool allow)
    {
      if (canSelect == allow) return;
      canSelect = allow;
      Cursor = allow ? Cursors.Cross : Cursors.Default;
      Invalidate();
    }

    public void SetWaterfallVisible(bool value)
    {
      if (showWaterfall == value) return;
      showWaterfall = value;
      Invalidate();
    }

    public void SetPeakHold(bool value)
    {
      peakHold = value;
      heldPeaks = null;
      Invalidate();
    }

    public void SetSpan(int hz)
    {
      int next = hz <= 1500 ? 1000 : hz <= 3000 ? 2000 :
        hz <= 8500 ? 6000 : 12000;
      if (spanHz == next) return;
      spanHz = next;
      SpanChanged?.Invoke(spanHz);
      Invalidate();
    }

    public void ClearHistory()
    {
      bins = null;
      heldPeaks = null;
      generation = long.MinValue;
      historyHead = -1;
      historyCount = 0;
      Array.Clear(historyLevels);
      using (Graphics g = Graphics.FromImage(history))
        g.Clear(Color.FromArgb(RsBa1WaterfallPalette[0]));
      Invalidate();
    }

    /// <summary>Original API retained for existing smoke tests and callers.</summary>
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

    public void SetMeasurement(double? observed, double target)
    {
      observedHz = observed;
      targetHz = target;
      Invalidate();
    }

    public void SetDisplayFrame(AfcIfDisplayFrame frame, double? observed,
      double target)
    {
      if (frame.RelativeDb is not { Length: > 1 }) return;
      if (generation != frame.Generation)
      {
        ClearHistory();
        generation = frame.Generation;
      }
      minHz = frame.MinHz;
      stepHz = frame.StepHz;
      bins = frame.RelativeDb;
      observedHz = observed;
      targetHz = target;
      if (peakHold)
      {
        if (heldPeaks == null || heldPeaks.Length != bins.Length)
          heldPeaks = (float[])bins.Clone();
        else
          for (int i = 0; i < bins.Length; i++)
            heldPeaks[i] = Math.Max(heldPeaks[i], bins[i]);
      }
      if (showWaterfall) AppendHistory(frame.RelativeDb);
      Invalidate();
    }

    private double VisibleMin => -spanHz / 2.0;
    private double VisibleMax => spanHz / 2.0;
    private bool HasWaterfall => showWaterfall && Height >= 210;
    private RectangleF Plot => new(58, 30, Math.Max(20, Width - 73),
      Math.Max(20, HasWaterfall ? (Height - 73) * .53f : Height - 73));
    private RectangleF WaterfallBounds
    {
      get
      {
        RectangleF trace = Plot;
        return new RectangleF(trace.Left, trace.Bottom + 9,
          trace.Width, Math.Max(0, Height - trace.Bottom - 38));
      }
    }

    private float ToX(double freq, RectangleF plot) =>
      (float)(plot.Left + (freq - VisibleMin) / spanHz * plot.Width);

    private double ToHz(double x, RectangleF plot) =>
      VisibleMin + Math.Clamp((x - plot.Left) / plot.Width, 0, 1) * spanHz;

    protected override void OnMouseDown(MouseEventArgs e)
    {
      base.OnMouseDown(e);
      if (e.Button == MouseButtons.Right)
      {
        SetSpan(12000);
        return;
      }
      if (!canSelect || e.Button != MouseButtons.Left ||
          bins is not { Length: > 8 }) return;
      RectangleF plot = Plot;
      if (plot.Contains(e.Location) ||
          HasWaterfall && WaterfallBounds.Contains(e.Location))
        TargetSelected?.Invoke(Math.Round(ToHz(e.X, plot), 1));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      int[] spans = { 1000, 2000, 6000, 12000 };
      int idx = Array.IndexOf(spans, spanHz);
      if (idx < 0) idx = 3;
      SetSpan(spans[Math.Clamp(idx + (e.Delta < 0 ? 1 : -1), 0, spans.Length - 1)]);
    }

    private float PeakAt(float[] values, double a, double b)
    {
      int min = Math.Clamp((int)Math.Floor((a - minHz) / stepHz), 0, values.Length - 1);
      int max = Math.Clamp((int)Math.Ceiling((b - minHz) / stepHz), min, values.Length - 1);
      float peak = float.NegativeInfinity;
      for (int i = min; i <= max; i++)
        peak = Math.Max(peak, values[i]);
      return float.IsFinite(peak) ? peak : DisplayBottom;
    }

    private void AppendHistory(float[] spectrum)
    {
      historyHead = historyHead < 0 ? HistoryRows - 1 :
        (historyHead + HistoryRows - 1) % HistoryRows;
      historyCount = Math.Min(HistoryRows, historyCount + 1);
      float[] raw = historyLevels[historyHead] ??= new float[HistoryWidth];
      for (int px = 0; px < HistoryWidth; px++)
      {
        double a = -6000 + px * (12000.0 / HistoryWidth);
        double b = -6000 + (px + 1) * (12000.0 / HistoryWidth);
        raw[px] = PeakAt(spectrum, a, b);
        linePixels[px] = MapWaterfallColor(raw[px]);
      }
      WriteHistoryRow(historyHead, linePixels);
    }

    // Reuse IcomLanSpectrumView's exact RS-BA1 Classic LUT:
    // navy -> blue -> cyan -> green/yellow -> white for high signal levels.
    // REF adjusts visual headroom only; IF signal measurements are
    // unaffected, and even light application themes retain a dark-blue
    // instrument floor rather than bleaching the waterfall.
    internal static int MapWaterfallLevel(double db, double referenceDb)
    {
      if (!double.IsFinite(db)) return 0;
      if (!double.IsFinite(referenceDb)) referenceDb = DefaultReferenceDb;
      referenceDb = Math.Clamp(referenceDb, ReferenceMinDb, ReferenceMaxDb);
      double floor = referenceDb - DefaultReferenceDb;
      return Math.Clamp((int)Math.Round((db - floor) * 160.0 / 50.0), 0, 160);
    }

    internal static int WaterfallArgbAt(double db, double referenceDb) =>
      RsBa1WaterfallPalette[MapWaterfallLevel(db, referenceDb)];

    private int MapWaterfallColor(float db) =>
      WaterfallArgbAt(db, referenceLevelDb);

    private void WriteHistoryRow(int row, int[] pixels)
    {
      BitmapData data = history.LockBits(
        new Rectangle(0, row, HistoryWidth, 1),
        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
      finally { history.UnlockBits(data); }
    }

    private void RebuildHistoryColors()
    {
      // Dispose the Graphics before LockBits: GDI+ forbids raster locks
      // while an active Graphics instance owns the same bitmap.
      using (Graphics g = Graphics.FromImage(history))
        g.Clear(Color.FromArgb(RsBa1WaterfallPalette[0]));
      for (int row = 0; row < HistoryRows; row++)
      {
        float[]? raw = historyLevels[row];
        if (raw == null) continue;
        for (int i = 0; i < HistoryWidth; i++)
          linePixels[i] = MapWaterfallColor(raw[i]);
        WriteHistoryRow(row, linePixels);
      }
    }

    private void PaintWaterfall(Graphics g, RectangleF destination)
    {
      if (historyCount == 0 || destination.Height < 5) return;
      g.InterpolationMode = InterpolationMode.NearestNeighbor;
      // All 180 bitmap rows are displayed with newest at the top, using
      // at most two DrawImage calls regardless of history length.
      double sourceLeft = (VisibleMin + 6000) / 12000 * HistoryWidth;
      double sourceWidth = spanHz / 12000.0 * HistoryWidth;
      int firstRows = HistoryRows - historyHead;
      float firstHeight = (float)(destination.Height * firstRows / HistoryRows);
      g.DrawImage(history,
        new RectangleF(destination.Left, destination.Top, destination.Width, firstHeight),
        new RectangleF((float)sourceLeft, historyHead, (float)sourceWidth, firstRows),
        GraphicsUnit.Pixel);
      if (historyHead > 0)
        g.DrawImage(history,
          new RectangleF(destination.Left, destination.Top + firstHeight,
            destination.Width, destination.Height - firstHeight),
          new RectangleF((float)sourceLeft, 0, (float)sourceWidth, historyHead),
          GraphicsUnit.Pixel);
      g.InterpolationMode = InterpolationMode.Default;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(SpectrumBackground);
      RectangleF plot = Plot;
      using var gridPen = new Pen(ScopeGrid);
      using var tracePen = new Pen(RsBa1Trace, 1.5f);
      using var peakPen = new Pen(RsBa1Peak, 1f) { DashStyle = DashStyle.Dot };
      using var centerPen = new Pen(RsBa1Peak, 1.4f)
        { DashStyle = DashStyle.Dash };
      using var observedPen = new Pen(Color.FromArgb(105, 236, 207), 1.25f);
      using var labelBrush = new SolidBrush(Color.FromArgb(166, 198, 223));
      using var titleBrush = new SolidBrush(ScopeText);
      using var fillBrush = new SolidBrush(Color.FromArgb(32, RsBa1Trace));
      g.DrawString($"12 kHz IF · REF {referenceLevelDb:+0;-0;0} dB · 频谱", Font,
        titleBrush, new PointF(plot.Left, 7));
      if (canSelect && plot.Width > 350)
      {
        string hint = "左键选台 · 滚轮缩放 · 右键复位";
        SizeF size = g.MeasureString(hint, Font);
        g.DrawString(hint, Font, labelBrush,
          new PointF(plot.Right - size.Width, 7));
      }

      for (int i = 0; i <= 5; i++)
      {
        float y = plot.Top + i * plot.Height / 5;
        g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
        g.DrawString((DisplayTop + (DisplayBottom - DisplayTop) * i / 5).ToString("0"),
          Font, labelBrush, 7, y - 7);
      }
      for (int i = 0; i <= 6; i++)
      {
        float x = plot.Left + i * plot.Width / 6;
        g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
        int offset = (int)Math.Round(VisibleMin + i * spanHz / 6.0);
        g.DrawString(offset.ToString("+0;-0;0"), Font, labelBrush,
          x - 22, Height - 26);
      }

      if (bins is { Length: > 8 } values)
      {
        int pixels = Math.Max(2, (int)plot.Width);
        var points = new PointF[pixels + 1];
        for (int i = 0; i <= pixels; i++)
        {
          double a = VisibleMin + (i - .5) * spanHz / pixels;
          double b = VisibleMin + (i + .5) * spanHz / pixels;
          float db = Math.Clamp(PeakAt(values, a, b), DisplayBottom, DisplayTop);
          points[i] = new PointF(plot.Left + i * plot.Width / pixels,
            plot.Bottom - (db - DisplayBottom) / VerticalRangeDb * plot.Height);
        }
        using var area = new GraphicsPath();
        area.AddLines(points);
        area.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
        area.AddLine(points[^1].X, plot.Bottom, points[0].X, plot.Bottom);
        area.CloseFigure();
        g.FillPath(fillBrush, area);
        g.DrawLines(tracePen, points);

        if (peakHold && heldPeaks is { Length: > 8 })
        {
          for (int i = 0; i <= pixels; i++)
          {
            double a = VisibleMin + (i - .5) * spanHz / pixels;
            double b = VisibleMin + (i + .5) * spanHz / pixels;
            float db = Math.Clamp(PeakAt(heldPeaks, a, b), DisplayBottom, DisplayTop);
            points[i] = new PointF(plot.Left + i * plot.Width / pixels,
              plot.Bottom - (db - DisplayBottom) / VerticalRangeDb * plot.Height);
          }
          g.DrawLines(peakPen, points);
        }
      }
      if (HasWaterfall)
      {
        var rect = WaterfallBounds;
        using var waterfallBackground = new SolidBrush(Color.FromArgb(RsBa1WaterfallPalette[0]));
        g.FillRectangle(waterfallBackground, rect);
        PaintWaterfall(g, rect);
        g.DrawRectangle(gridPen, rect.Left, rect.Top, rect.Width, rect.Height);
      }
      float zero = ToX(0, plot);
      if (zero >= plot.Left && zero <= plot.Right)
      {
        g.DrawLine(centerPen, zero, plot.Top, zero, plot.Bottom);
        if (HasWaterfall)
          g.DrawLine(centerPen, zero, WaterfallBounds.Top, zero, WaterfallBounds.Bottom);
      }
      if (canSelect)
      {
        float targetX = ToX(targetHz, plot);
        if (targetX >= plot.Left && targetX <= plot.Right && Math.Abs(targetX - zero) > 3)
        {
          using var p = new Pen(palette.Accent, 1f);
          g.DrawLine(p, targetX, plot.Top, targetX, plot.Bottom);
        }
      }
      if (observedHz is double hz && double.IsFinite(hz))
      {
        float x = ToX(hz, plot);
        if (x >= plot.Left && x <= plot.Right)
          g.DrawLine(observedPen, x, plot.Top, x, plot.Bottom);
      }
      g.DrawRectangle(gridPen, plot.Left, plot.Top, plot.Width, plot.Height);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing) history.Dispose();
      base.Dispose(disposing);
    }
  }
}
