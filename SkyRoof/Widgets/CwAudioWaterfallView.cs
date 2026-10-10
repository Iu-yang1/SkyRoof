using SkyRoof.CW;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkyRoof
{
  public sealed class CwWaterfallLaneClickedEventArgs : EventArgs
  {
    public CwConsoleLaneIdentity Identity { get; }
    public double FrequencyHz { get; }

    public CwWaterfallLaneClickedEventArgs(
      CwConsoleLaneIdentity identity,
      double frequencyHz)
    {
      Identity = identity;
      FrequencyHz = frequencyHz;
    }
  }

  /// <summary>
  /// CW Skimmer-style horizontal-time waterfall. Time advances leftward as
  /// new frames enter on the right, while frequency is vertical on the left.
  /// The display-only viewport never changes decoder/receive input.
  /// </summary>
  public sealed class CwAudioWaterfallView : Control
  {
    private const int FrequencyScaleWidth = 57;
    private const int SpectrumTraceWidth = 51;
    private const int HistoryColumns = 512;
    private const double DisplayRangeDb = 30.0;

    private static readonly Color SkimmerBackground =
      Color.FromArgb(0, 15, 46);
    private static readonly Color SkimmerScaleBackground =
      Color.FromArgb(9, 31, 55);
    private static readonly Color SkimmerGrid =
      Color.FromArgb(35, 79, 101);
    private static readonly Color SkimmerTrace =
      Color.FromArgb(224, 236, 241);
    private static readonly Color SkimmerLane =
      Color.FromArgb(65, 220, 238);
    private static readonly Color SkimmerPeak =
      Color.FromArgb(65, 255, 112);

    private readonly Bitmap waterfall;
    private int nextWriteColumn;
    private bool hasRows;
    private long frameRevision;
    private long lastPaintedFrameRevision;
    private readonly CwDisplayCadenceMeter paintCadence = new();

    internal CwDisplayCadenceSnapshot PaintMetrics =>
      paintCadence.Snapshot();
    private float displayFloorDb = float.NaN;
    private float spectrumFloorDb = float.NaN;
    private float[] latestPowerDb = Array.Empty<float>();
    private double minFrequencyHz = 100;
    private double maxFrequencyHz = 2000;
    private double viewportStartFraction;
    private double viewportZoom = 1.0;

    // Vertical AF pan/zoom changes the display only; the detector and the
    // complete wideband inference still receive the full original AF span.
    public double ViewportStartFraction => viewportStartFraction;
    public double ViewportZoom => viewportZoom;
    public double VisibleMinimumHz =>
      minFrequencyHz + viewportStartFraction *
      (maxFrequencyHz - minFrequencyHz) *
      (1.0 - 1.0 / viewportZoom);
    public double VisibleMaximumHz =>
      VisibleMinimumHz +
      (maxFrequencyHz - minFrequencyHz) / viewportZoom;
    public event EventHandler? ViewportChanged;

    public void SetViewport(double startFraction, double zoom)
    {
      if (!double.IsFinite(startFraction) || !double.IsFinite(zoom))
        throw new ArgumentOutOfRangeException(nameof(zoom));

      double nextZoom = Math.Clamp(zoom, 1.0, 8.0);
      double nextStart = nextZoom == 1.0
        ? 0.0 : Math.Clamp(startFraction, 0.0, 1.0);
      if (Math.Abs(nextZoom - viewportZoom) < 1e-9 &&
          Math.Abs(nextStart - viewportStartFraction) < 1e-9)
        return;

      viewportZoom = nextZoom;
      viewportStartFraction = nextStart;
      Invalidate();
      ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
      // Focus on explicit interaction, never on hover: RX spectrum hover
      // must not steal keyboard focus from a live CW TX composer.
      Focus();
      base.OnMouseDown(e);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      if (ModifierKeys.HasFlag(Keys.Control))
      {
        // Keep wheel zoom aligned with the integer 1–8x toolbar control.
        SetViewport(viewportStartFraction,
          Math.Clamp(
            Math.Round(viewportZoom) + Math.Sign(e.Delta),
            1, 8));
      }
      else
      {
        SetViewport(viewportStartFraction -
          Math.Sign(e.Delta) * 0.08, viewportZoom);
      }
    }

    private IReadOnlyList<CwSignalTrack> tracks =
      Array.Empty<CwSignalTrack>();
    private CwConsoleLaneIdentity? selectedIdentity;

    public event EventHandler<CwWaterfallLaneClickedEventArgs>?
      LaneClicked;

    public CwAudioWaterfallView(
      int spectrumBins = 384)
    {
      if (spectrumBins < 32)
        throw new ArgumentOutOfRangeException(
          nameof(spectrumBins));

      DoubleBuffered = true;
      TabStop = true;
      MinimumSize = new Size(250, 130);
      // X is history/time, Y is frequency. The low AF bins occupy the
      // bottom of the bitmap; the most recent column is drawn on the right.
      waterfall =
        new Bitmap(
          HistoryColumns,
          spectrumBins,
          PixelFormat.Format32bppArgb);

      Clear();
      MouseClick +=
        CwAudioWaterfallView_MouseClick;
    }

    public void Clear()
    {
      using Graphics g =
        Graphics.FromImage(
          waterfall);
      g.Clear(
        SkimmerBackground);
      nextWriteColumn = 0;
      hasRows = false;
      frameRevision = 0;
      lastPaintedFrameRevision = 0;
      paintCadence.Reset();
      displayFloorDb = float.NaN;
      spectrumFloorDb = float.NaN;
      latestPowerDb = Array.Empty<float>();
      Invalidate();
    }

    public void SetSpectrum(
      CwAudioSpectrumFrame frame)
    {
      if (frame.PowerDb.Length !=
          waterfall.Height)
        throw new ArgumentException(
          "Spectrum width changed.",
          nameof(frame));

      minFrequencyHz =
        frame.MinFrequencyHz;
      maxFrequencyHz =
        frame.MaxFrequencyHz;
      latestPowerDb =
        (float[])frame.PowerDb.Clone();
      float targetFloor =
        Percentile(
          frame.PowerDb,
          0.35);
      spectrumFloorDb =
        float.IsFinite(spectrumFloorDb)
          ? 0.90f * spectrumFloorDb +
            0.10f * targetFloor
          : targetFloor;
      Invalidate();
    }

    public void Append(
      CwAudioSpectrumFrame frame)
    {
      if (frame.PowerDb.Length !=
          waterfall.Height)
        throw new ArgumentException(
          "Waterfall spectrum width changed.",
          nameof(frame));

      minFrequencyHz =
        frame.MinFrequencyHz;
      maxFrequencyHz =
        frame.MaxFrequencyHz;
      float targetFloor =
        Percentile(
          frame.PowerDb,
          0.35);
      displayFloorDb =
        float.IsFinite(displayFloorDb)
          ? 0.90f * displayFloorDb +
            0.10f * targetFloor
          : targetFloor;

      // Unlike the old per-frame min/max stretch, keep a stable CW-oriented
      // dynamic range. Noise stays dark while a 6-10 dB keyed carrier is
      // already visible; strong carriers progress toward yellow/white.
      double floor =
        displayFloorDb + 1.0;

      // Update one 32-bit column under a single bitmap lock instead of
      // making 512 GDI+ SetPixel calls on every 50-ms UI paint tick.
      BitmapData pixels = waterfall.LockBits(
        new Rectangle(0, 0, waterfall.Width, waterfall.Height),
        ImageLockMode.WriteOnly,
        PixelFormat.Format32bppArgb);
      try
      {
        for (int bin = 0; bin < waterfall.Height; bin++)
        {
          double level = Math.Pow(
            Math.Clamp(
              (frame.PowerDb[bin] - floor) / DisplayRangeDb,
              0, 1),
            0.78);
          int argb = HeatColor(level).ToArgb();
          Marshal.WriteInt32(
            pixels.Scan0,
            (waterfall.Height - 1 - bin) * pixels.Stride +
              nextWriteColumn * 4,
            argb);
        }
      }
      finally
      {
        waterfall.UnlockBits(pixels);
      }

      nextWriteColumn =
        (nextWriteColumn + 1) % HistoryColumns;

      hasRows = true;
      frameRevision++;
      Invalidate();
    }

    public void SetTracks(
      IEnumerable<CwSignalTrack> value,
      CwConsoleLaneIdentity? selected)
    {
      ArgumentNullException.ThrowIfNull(value);
      tracks =
        value
          .Where(x =>
            double.IsFinite(
              x.FrequencyHz))
          .OrderBy(x =>
            x.FrequencyHz)
          .ToArray();
      selectedIdentity =
        selected;
      Invalidate();
    }

    // Frequency increases upward; chronological history scrolls toward
    // the left. These helpers are shared by drawing and hit-testing.
    internal static float FrequencyToVerticalPixel(
      double hz, double lowHz, double highHz, int height)
    {
      double proportion =
        (hz - lowHz) / Math.Max(highHz - lowHz, 1e-9);
      return (float)((1 - Math.Clamp(proportion, 0, 1)) *
        Math.Max(1, height - 1));
    }

    internal static double VerticalPixelToFrequency(
      int y, double lowHz, double highHz, int height)
    {
      double proportion = 1 - Math.Clamp(
        y / (double)Math.Max(1, height - 1), 0, 1);
      return lowHz + proportion * (highHz - lowHz);
    }

    private Rectangle WaterfallBounds =>
      new(
        FrequencyScaleWidth + SpectrumTraceWidth,
        0,
        Math.Max(1, ClientSize.Width -
          FrequencyScaleWidth - SpectrumTraceWidth),
        Math.Max(1, ClientSize.Height));

    private float FrequencyToY(double frequencyHz) =>
      FrequencyToVerticalPixel(
        frequencyHz, VisibleMinimumHz, VisibleMaximumHz,
        ClientSize.Height);

    private double YToFrequency(int y) =>
      VerticalPixelToFrequency(
        y, VisibleMinimumHz, VisibleMaximumHz,
        ClientSize.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      PaintContents(e.Graphics);
      // Count actual newly-painted waterfall columns, not timer callbacks
      // or invalidate requests (which WinForms may merge under load).
      if (hasRows && frameRevision != lastPaintedFrameRevision)
      {
        lastPaintedFrameRevision = frameRevision;
        paintCadence.Record();
      }
    }

    // Shared by the real Control paint path and raster tests, without
    // relying on Control.DrawToBitmap/WM_PRINTCLIENT semantics.
    internal void PaintContents(Graphics graphics)
    {
      Rectangle scale = new(
        0, 0, FrequencyScaleWidth,
        Math.Max(1, ClientSize.Height));
      Rectangle spectrum = new(
        scale.Right, 0, SpectrumTraceWidth,
        Math.Max(1, ClientSize.Height));
      Rectangle body = WaterfallBounds;

      using (var scaleBrush =
        new SolidBrush(SkimmerScaleBackground))
        graphics.FillRectangle(scaleBrush, scale);
      using (var background =
        new SolidBrush(SkimmerBackground))
        graphics.FillRectangle(background, spectrum);
      using (var background =
        new SolidBrush(SkimmerBackground))
        graphics.FillRectangle(background, body);

      if (hasRows)
        DrawWaterfall(graphics, body);
      DrawFrequencyScale(graphics, scale, spectrum, body);
      DrawSpectrumTrace(graphics, spectrum);
      DrawTrackMarkers(graphics, body);
    }

    private void DrawSpectrumTrace(
      Graphics g, Rectangle spectrum)
    {
      if (latestPowerDb.Length < 2 ||
          !float.IsFinite(spectrumFloorDb))
        return;

      using var pen = new Pen(SkimmerTrace, 1.2f);
      var points = new PointF[latestPowerDb.Length];
      double floor = spectrumFloorDb + 1.0;
      for (int bin = 0; bin < latestPowerDb.Length; bin++)
      {
        double hz = minFrequencyHz +
          bin * (maxFrequencyHz - minFrequencyHz) /
          (latestPowerDb.Length - 1);
        double relative = Math.Clamp(
          (latestPowerDb[bin] - floor) / DisplayRangeDb,
          0, 1);
        float x = spectrum.Right - 2 -
          (float)(relative * Math.Max(1, spectrum.Width - 5));
        points[bin] = new PointF(x, FrequencyToY(hz));
      }

      GraphicsState saved = g.Save();
      g.SetClip(spectrum);
      g.DrawLines(pen, points);
      g.Restore(saved);
    }

    private void DrawWaterfall(Graphics g, Rectangle body)
    {
      // The ring is stored as columns: each new 512-bin FFT is one time
      // column. Split at nextWriteColumn (oldest) to place the newest
      // sample at the right edge with no image rotation or transposition
      // during paint. A blank ring shows dark space to the left of new data.
      int firstCount = HistoryColumns - nextWriteColumn;
      int secondCount = nextWriteColumn;

      double fullSpan = Math.Max(1e-9,
        maxFrequencyHz - minFrequencyHz);
      int top = Math.Clamp(
        (int)Math.Floor(
          (maxFrequencyHz - VisibleMaximumHz) / fullSpan *
          waterfall.Height), 0, waterfall.Height - 1);
      int bottom = Math.Clamp(
        (int)Math.Ceiling(
          (maxFrequencyHz - VisibleMinimumHz) / fullSpan *
          waterfall.Height), top + 1, waterfall.Height);
      int sourceHeight = bottom - top;

      GraphicsState saved = g.Save();
      g.SetClip(body);
      g.InterpolationMode = InterpolationMode.NearestNeighbor;
      g.PixelOffsetMode = PixelOffsetMode.Half;
      int leftWidth = (int)Math.Round(
        firstCount * body.Width / (double)HistoryColumns);
      if (firstCount > 0)
      {
        g.DrawImage(
          waterfall,
          new Rectangle(body.Left, body.Top,
            Math.Max(1, leftWidth), body.Height),
          new Rectangle(nextWriteColumn, top,
            firstCount, sourceHeight),
          GraphicsUnit.Pixel);
      }
      if (secondCount > 0)
      {
        g.DrawImage(
          waterfall,
          new Rectangle(body.Left + leftWidth, body.Top,
            Math.Max(1, body.Width - leftWidth), body.Height),
          new Rectangle(0, top, secondCount, sourceHeight),
          GraphicsUnit.Pixel);
      }
      g.Restore(saved);
    }

    private void DrawFrequencyScale(
      Graphics g, Rectangle scale, Rectangle spectrum,
      Rectangle body)
    {
      int stepHz = viewportZoom >= 4 ? 50 :
        viewportZoom >= 2 ? 100 : 250;
      int first = (int)Math.Ceiling(
        VisibleMinimumHz / stepHz) * stepHz;
      using var pen = new Pen(SkimmerGrid);
      using var brush = new SolidBrush(Color.Gainsboro);
      for (int hz = first; hz <= VisibleMaximumHz; hz += stepHz)
      {
        float y = FrequencyToY(hz);
        g.DrawLine(pen, scale.Right - 5, y, body.Right, y);
        string caption = hz.ToString();
        SizeF size = g.MeasureString(caption, Font);
        float labelY = Math.Clamp(
          y - size.Height / 2, scale.Top,
          Math.Max(scale.Top, scale.Bottom - size.Height));
        g.DrawString(caption, Font, brush,
          scale.Right - size.Width - 6, labelY);
      }
      using var border = new Pen(Color.FromArgb(90, 145, 167));
      g.DrawLine(border, scale.Right, 0,
        scale.Right, ClientSize.Height);
      g.DrawLine(border, spectrum.Right, 0,
        spectrum.Right, ClientSize.Height);
    }

    private void DrawTrackMarkers(
      Graphics g, Rectangle body)
    {
      using var normalPen = new Pen(
        Color.FromArgb(185, SkimmerLane), 1.25f);
      using var selectedPen = new Pen(SkimmerPeak, 2.4f);
      using var ambiguousPen = new Pen(Color.Orange, 1.35f)
      { DashStyle = DashStyle.Dash };
      using var holdPen = new Pen(
        Color.FromArgb(145, Color.LightGray), 1.0f)
      { DashStyle = DashStyle.Dot };
      var labelEnds = new float[]
        { float.NegativeInfinity, float.NegativeInfinity,
          float.NegativeInfinity };

      GraphicsState saved = g.Save();
      g.SetClip(body);
      foreach (CwSignalTrack track in tracks)
      {
        if (track.FrequencyHz < VisibleMinimumHz ||
            track.FrequencyHz > VisibleMaximumHz)
          continue;

        CwConsoleLaneIdentity id =
          CwConsolePresentation.Identity(track);
        bool selected = selectedIdentity.HasValue &&
          id == selectedIdentity.Value;
        Pen pen = selected ? selectedPen :
          track.Ambiguous ? ambiguousPen :
          !track.Active ? holdPen : normalPen;

        float y = FrequencyToY(track.FrequencyHz);
        if (selected && track.FrequencySigmaHz > 0 &&
            double.IsFinite(track.FrequencySigmaHz))
        {
          float first = FrequencyToY(
            track.FrequencyHz + 2 * track.FrequencySigmaHz);
          float last = FrequencyToY(
            track.FrequencyHz - 2 * track.FrequencySigmaHz);
          using var fill = new SolidBrush(
            Color.FromArgb(34, SkimmerPeak));
          g.FillRectangle(fill, body.Left, Math.Min(first, last),
            body.Width, Math.Max(1, Math.Abs(last - first)));
        }

        g.DrawLine(pen, body.Left, y, body.Right, y);
        string label = CwConsolePresentation.LaneLabel(track);
        SizeF textSize = g.MeasureString(label, Font);
        int col = 0;
        for (int i = 0; i < labelEnds.Length; i++)
        {
          if (y > labelEnds[i] + 3)
          {
            col = i;
            break;
          }
          col = i;
        }

        float labelX = body.Left + 5 +
          col * (textSize.Width + 9);
        float labelY = Math.Clamp(y - textSize.Height - 2,
          body.Top, Math.Max(body.Top,
          body.Bottom - textSize.Height - 4));
        labelEnds[col] = labelY + textSize.Height;
        using var background = new SolidBrush(
          Color.FromArgb(220, SkimmerBackground));
        using var foreground = new SolidBrush(pen.Color);
        g.FillRectangle(background, labelX, labelY,
          textSize.Width + 5, textSize.Height + 2);
        g.DrawString(label, Font, foreground,
          labelX + 2, labelY + 1);
      }
      g.Restore(saved);
    }

    private void CwAudioWaterfallView_MouseClick(
      object? sender, MouseEventArgs e)
    {
      if (e.Button != MouseButtons.Left ||
          e.X < WaterfallBounds.Left || tracks.Count == 0)
        return;

      double frequency = YToFrequency(e.Y);
      CwSignalTrack? nearest = tracks
        .Where(track =>
          track.FrequencyHz >= VisibleMinimumHz &&
          track.FrequencyHz <= VisibleMaximumHz)
        .OrderBy(track =>
          Math.Abs(track.FrequencyHz - frequency))
        .Select(track => (CwSignalTrack?)track)
        .FirstOrDefault();
      if (nearest is not CwSignalTrack matched)
        return;

      double hzPerPixel =
        (VisibleMaximumHz - VisibleMinimumHz) /
        Math.Max(1, ClientSize.Height);
      double gate = Math.Max(35, hzPerPixel * 18);
      if (Math.Abs(matched.FrequencyHz - frequency) > gate)
        return;

      LaneClicked?.Invoke(this,
        new CwWaterfallLaneClickedEventArgs(
          CwConsolePresentation.Identity(matched),
          matched.FrequencyHz));
    }

    private static float Percentile(
      float[] values,
      double percentile)
    {
      float[] copy =
        (float[])values.Clone();
      Array.Sort(copy);

      double p =
        Math.Clamp(
          percentile,
          0,
          1) *
        (copy.Length - 1);
      int lower =
        (int)Math.Floor(p);
      int upper =
        Math.Min(
          copy.Length - 1,
          lower + 1);
      double mix =
        p - lower;

      return (float)(
        copy[lower] *
          (1 - mix) +
        copy[upper] * mix);
    }

    private static Color HeatColor(
      double level)
    {
      level =
        Math.Clamp(
          level,
          0,
          1);

      Color blue =
        Color.FromArgb(0, 35, 112);
      Color cyan =
        Color.FromArgb(0, 176, 226);
      Color green =
        Color.FromArgb(0, 225, 70);
      Color yellow =
        Color.FromArgb(255, 225, 35);
      Color red =
        Color.FromArgb(255, 75, 32);

      if (level < 0.16)
        return Mix(
          SkimmerBackground,
          blue,
          level / 0.16);
      if (level < 0.42)
        return Mix(
          blue,
          cyan,
          (level - 0.16) /
          0.26);
      if (level < 0.68)
        return Mix(
          cyan,
          green,
          (level - 0.42) /
          0.26);
      if (level < 0.86)
        return Mix(
          green,
          yellow,
          (level - 0.68) /
          0.18);
      if (level < 0.96)
        return Mix(
          yellow,
          red,
          (level - 0.86) /
          0.10);

      return Mix(
        red,
        Color.White,
        (level - 0.96) /
        0.04);
    }

    private static Color Mix(
      Color a,
      Color b,
      double t)
    {
      t =
        Math.Clamp(
          t,
          0,
          1);

      return Color.FromArgb(
        255,
        (int)Math.Round(
          a.R +
          (b.R - a.R) * t),
        (int)Math.Round(
          a.G +
          (b.G - a.G) * t),
        (int)Math.Round(
          a.B +
          (b.B - a.B) * t));
    }

    protected override void Dispose(
      bool disposing)
    {
      if (disposing)
      {
        MouseClick -=
          CwAudioWaterfallView_MouseClick;
        waterfall.Dispose();
      }

      base.Dispose(disposing);
    }
  }
}
