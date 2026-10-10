using SkyRoof.CW;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

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
  /// Small receive-only AF waterfall. Spectrum rows are supplied by the
  /// display analyzer; lane overlays come from immutable tracker snapshots.
  /// </summary>
  public sealed class CwAudioWaterfallView : Control
  {
    private const int ScaleHeight = 26;
    private const int SpectrumHeight = 44;
    private const int HistoryRows = 180;
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
    private int writeRow;
    private bool hasRows;
    private float displayFloorDb = float.NaN;
    private float spectrumFloorDb = float.NaN;
    private float[] latestPowerDb = Array.Empty<float>();
    private double minFrequencyHz = 100;
    private double maxFrequencyHz = 2000;
    private double viewportStartFraction;
    private double viewportZoom = 1.0;

    // Horizontal pan/zoom changes the display only; the detector and the
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

    protected override void OnMouseWheel(MouseEventArgs e)
    {
      base.OnMouseWheel(e);
      if (ModifierKeys.HasFlag(Keys.Control))
      {
        SetViewport(viewportStartFraction,
          viewportZoom * (e.Delta > 0 ? 1.25 : 0.8));
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
      MouseEnter += (_, _) => Focus();
      waterfall =
        new Bitmap(
          spectrumBins,
          HistoryRows,
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
      writeRow = 0;
      hasRows = false;
      displayFloorDb = float.NaN;
      spectrumFloorDb = float.NaN;
      latestPowerDb = Array.Empty<float>();
      Invalidate();
    }

    public void SetSpectrum(
      CwAudioSpectrumFrame frame)
    {
      if (frame.PowerDb.Length !=
          waterfall.Width)
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
          waterfall.Width)
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

      writeRow =
        (writeRow - 1 +
         HistoryRows) %
        HistoryRows;

      for (int x = 0;
           x < waterfall.Width;
           x++)
      {
        double level =
          (frame.PowerDb[x] -
           floor) /
          DisplayRangeDb;
        level =
          Math.Pow(
            Math.Clamp(
              level,
              0,
              1),
            0.78);

        waterfall.SetPixel(
          x,
          writeRow,
          HeatColor(level));
      }

      hasRows = true;
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

    protected override void OnPaint(
      PaintEventArgs e)
    {
      base.OnPaint(e);

      Rectangle scale =
        new(
          0,
          0,
          ClientSize.Width,
          ScaleHeight);
      Rectangle spectrum =
        new(
          0,
          ScaleHeight,
          ClientSize.Width,
          Math.Min(
            SpectrumHeight,
            Math.Max(
              1,
              ClientSize.Height -
              ScaleHeight)));
      Rectangle waterfallBody =
        new(
          0,
          spectrum.Bottom,
          ClientSize.Width,
          Math.Max(
            1,
            ClientSize.Height -
            spectrum.Bottom));
      Rectangle plot =
        new(
          0,
          ScaleHeight,
          ClientSize.Width,
          Math.Max(
            1,
            ClientSize.Height -
            ScaleHeight));

      using (var scaleBrush =
        new SolidBrush(
          SkimmerScaleBackground))
        e.Graphics.FillRectangle(
          scaleBrush,
          scale);
      using (var background =
        new SolidBrush(
          SkimmerBackground))
      {
        e.Graphics.FillRectangle(
          background,
          plot);
      }

      DrawScale(
        e.Graphics,
        scale,
        plot);
      DrawSpectrumTrace(
        e.Graphics,
        spectrum);

      if (hasRows)
        DrawWaterfall(
          e.Graphics,
          waterfallBody);

      DrawTrackMarkers(
        e.Graphics,
        plot);
    }

    private void DrawSpectrumTrace(
      Graphics g,
      Rectangle body)
    {
      if (latestPowerDb.Length < 2 ||
          !float.IsFinite(spectrumFloorDb))
        return;

      using var pen = new Pen(SkimmerTrace, 1.25f);
      double floor = spectrumFloorDb + 1.0;
      int first = Math.Clamp(
        (int)Math.Floor(
          (VisibleMinimumHz - minFrequencyHz) /
          Math.Max(1e-9, maxFrequencyHz - minFrequencyHz) *
          (latestPowerDb.Length - 1)),
        0, latestPowerDb.Length - 2);
      int last = Math.Clamp(
        (int)Math.Ceiling(
          (VisibleMaximumHz - minFrequencyHz) /
          Math.Max(1e-9, maxFrequencyHz - minFrequencyHz) *
          (latestPowerDb.Length - 1)),
        first + 1, latestPowerDb.Length - 1);
      var points = new PointF[last - first + 1];

      for (int i = first; i <= last; i++)
      {
        double level = Math.Clamp(
          (latestPowerDb[i] - floor) / DisplayRangeDb,
          0, 1);
        double frequency = minFrequencyHz +
          i * (maxFrequencyHz - minFrequencyHz) /
          (latestPowerDb.Length - 1);
        float x = FrequencyToX(frequency);
        float y = body.Bottom - 2 -
          (float)(level * Math.Max(1, body.Height - 5));
        points[i - first] = new PointF(x, y);
      }
      g.SetClip(body);
      g.DrawLines(pen, points);
      g.ResetClip();
    }

    private void DrawWaterfall(
      Graphics g,
      Rectangle body)
    {
      g.InterpolationMode =
        InterpolationMode.NearestNeighbor;
      g.PixelOffsetMode =
        PixelOffsetMode.Half;

      // New rows are written backwards. writeRow therefore always points
      // at the newest row and the chronological ring starts there.
      int sourceLeft = Math.Clamp(
        (int)Math.Floor(
          (VisibleMinimumHz - minFrequencyHz) /
          Math.Max(1e-9, maxFrequencyHz - minFrequencyHz) *
          waterfall.Width),
        0, waterfall.Width - 1);
      int sourceRight = Math.Clamp(
        (int)Math.Ceiling(
          (VisibleMaximumHz - minFrequencyHz) /
          Math.Max(1e-9, maxFrequencyHz - minFrequencyHz) *
          waterfall.Width),
        sourceLeft + 1, waterfall.Width);
      int sourceWidth = sourceRight - sourceLeft;

      int firstCount =
        HistoryRows - writeRow;
      int secondCount =
        writeRow;

      if (firstCount > 0)
      {
        Rectangle source =
          new(
            sourceLeft,
            writeRow,
            sourceWidth,
            firstCount);
        Rectangle dest =
          new(
            body.Left,
            body.Top,
            body.Width,
            (int)Math.Ceiling(
              firstCount *
              body.Height /
              (double)HistoryRows));
        g.DrawImage(
          waterfall,
          dest,
          source,
          GraphicsUnit.Pixel);
      }

      if (secondCount > 0)
      {
        Rectangle source =
          new(
            sourceLeft,
            0,
            sourceWidth,
            secondCount);
        int top =
          body.Top +
          (int)Math.Round(
            firstCount *
            body.Height /
            (double)HistoryRows);
        Rectangle dest =
          new(
            body.Left,
            top,
            body.Width,
            body.Bottom - top);
        g.DrawImage(
          waterfall,
          dest,
          source,
          GraphicsUnit.Pixel);
      }
    }

    private void DrawScale(
      Graphics g,
      Rectangle scale,
      Rectangle plot)
    {
      int stepHz = viewportZoom >= 4.0 ? 50
        : viewportZoom >= 2.0 ? 100 : 250;
      int first =
        (int)Math.Ceiling(
          VisibleMinimumHz /
          stepHz) *
        stepHz;

      using var gridPen =
        new Pen(
          SkimmerGrid);
      using var textBrush =
        new SolidBrush(
          Color.Gainsboro);

      for (int hz = first;
           hz <= VisibleMaximumHz;
           hz += stepHz)
      {
        float x =
          FrequencyToX(hz);
        g.DrawLine(
          gridPen,
          x,
          scale.Bottom - 8,
          x,
          plot.Bottom);

        string label =
          hz.ToString();
        SizeF size =
          g.MeasureString(
            label,
            Font);
        g.DrawString(
          label,
          Font,
          textBrush,
          x - size.Width / 2,
          2);
      }
    }

    private void DrawTrackMarkers(
      Graphics g,
      Rectangle body)
    {
      using var normalPen =
        new Pen(
          Color.FromArgb(
            185,
            SkimmerLane),
          1.25f);
      using var selectedPen =
        new Pen(
          SkimmerPeak,
          2.4f);
      using var ambiguousPen =
        new Pen(
          Color.Orange,
          1.35f)
        {
          DashStyle =
            DashStyle.Dash
        };
      using var holdPen =
        new Pen(
          Color.FromArgb(
            145,
            Color.LightGray),
          1.0f)
        {
          DashStyle =
            DashStyle.Dot
        };

      float[] labelRight =
        { float.NegativeInfinity,
          float.NegativeInfinity,
          float.NegativeInfinity };

      foreach (CwSignalTrack track
        in tracks)
      {
        if (track.FrequencyHz <
              VisibleMinimumHz ||
            track.FrequencyHz >
              VisibleMaximumHz)
          continue;

        CwConsoleLaneIdentity identity =
          CwConsolePresentation.Identity(
            track);
        bool selected =
          selectedIdentity.HasValue &&
          identity ==
            selectedIdentity.Value;

        Pen pen =
          selected
            ? selectedPen
            : track.Ambiguous
              ? ambiguousPen
              : !track.Active
                ? holdPen
                : normalPen;

        float x =
          FrequencyToX(
            track.FrequencyHz);

        // Keep the full-height uncertainty band only for the selected lane.
        // Drawing every ±2σ band was obscuring the actual CW spectrum.
        if (selected &&
            track.FrequencySigmaHz > 0 &&
            double.IsFinite(
              track.FrequencySigmaHz))
        {
          float lowX =
            FrequencyToX(
              track.FrequencyHz -
              2 * track.FrequencySigmaHz);
          float highX =
            FrequencyToX(
              track.FrequencyHz +
              2 * track.FrequencySigmaHz);
          float left =
            Math.Min(lowX, highX);
          float width =
            Math.Max(
              1,
              Math.Abs(
                highX - lowX));
          using var sigmaBrush =
            new SolidBrush(
              Color.FromArgb(
                34,
                SkimmerPeak));
          g.FillRectangle(
            sigmaBrush,
            left,
            body.Top,
            width,
            body.Height);
        }

        g.DrawLine(
          pen,
          x,
          body.Top,
          x,
          body.Bottom);

        string lane =
          CwConsolePresentation.LaneLabel(
            track);
        SizeF size =
          g.MeasureString(
            lane,
            Font);

        int labelRow = 0;
        for (int row = 0;
             row < labelRight.Length;
             row++)
        {
          if (x > labelRight[row] + 5)
          {
            labelRow = row;
            break;
          }
          labelRow = row;
        }

        float labelX =
          Math.Clamp(
            x + 3,
            body.Left + 1,
            Math.Max(
              body.Left + 1,
              body.Right -
              size.Width - 7));
        float labelY =
          body.Top + 2 +
          labelRow *
          (size.Height + 2);
        RectangleF labelRect =
          new(
            labelX,
            labelY,
            size.Width + 5,
            size.Height + 2);
        labelRight[labelRow] =
          labelRect.Right;

        using var backBrush =
          new SolidBrush(
            Color.FromArgb(
              220,
              SkimmerBackground));
        using var textBrush =
          new SolidBrush(
            pen.Color);
        g.FillRectangle(
          backBrush,
          labelRect);
        g.DrawString(
          lane,
          Font,
          textBrush,
          labelRect.Left + 2,
          labelRect.Top + 1);
      }
    }

    private void CwAudioWaterfallView_MouseClick(
      object? sender,
      MouseEventArgs e)
    {
      if (e.Button !=
          MouseButtons.Left ||
          e.Y < ScaleHeight ||
          tracks.Count == 0)
        return;

      double frequency =
        XToFrequency(e.X);

      CwSignalTrack[] visible =
        tracks
          .Where(x =>
            x.FrequencyHz >=
              VisibleMinimumHz &&
            x.FrequencyHz <=
              VisibleMaximumHz)
          .OrderBy(x =>
            Math.Abs(
              x.FrequencyHz -
              frequency))
          .ToArray();

      if (visible.Length == 0)
        return;

      CwSignalTrack nearest =
        visible[0];

      double hzPerPixel =
        (VisibleMaximumHz -
         VisibleMinimumHz) /
        Math.Max(
          1,
          ClientSize.Width);
      double pickGateHz =
        Math.Max(
          35,
          hzPerPixel * 18);

      if (Math.Abs(
            nearest.FrequencyHz -
            frequency) >
          pickGateHz)
        return;

      LaneClicked?.Invoke(
        this,
        new CwWaterfallLaneClickedEventArgs(
          CwConsolePresentation.Identity(
            nearest),
          nearest.FrequencyHz));
    }

    private float FrequencyToX(
      double frequencyHz)
    {
      double fraction =
        (frequencyHz -
         VisibleMinimumHz) /
        Math.Max(
          VisibleMaximumHz -
          VisibleMinimumHz,
          1e-9);

      return (float)(
        Math.Clamp(
          fraction,
          0,
          1) *
        Math.Max(
          1,
          ClientSize.Width - 1));
    }

    private double XToFrequency(
      int x)
    {
      double fraction =
        Math.Clamp(
          x /
          (double)Math.Max(
            1,
            ClientSize.Width - 1),
          0,
          1);

      return VisibleMinimumHz +
        fraction *
        (VisibleMaximumHz -
         VisibleMinimumHz);
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
