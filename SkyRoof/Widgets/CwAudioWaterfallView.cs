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
    private const int HistoryRows = 180;

    private readonly Bitmap waterfall;
    private int writeRow;
    private bool hasRows;
    private double minFrequencyHz = 100;
    private double maxFrequencyHz = 2000;

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
      MinimumSize = new Size(320, 120);
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
        Theme.SpectrumBackground);
      writeRow = 0;
      hasRows = false;
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

      float floor =
        Percentile(
          frame.PowerDb,
          0.20);
      float ceiling =
        Math.Max(
          floor + 20,
          Percentile(
            frame.PowerDb,
            0.985));

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
          Math.Max(
            ceiling - floor,
            1e-6);
        level =
          Math.Clamp(
            level,
            0,
            1);

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
      Rectangle body =
        new(
          0,
          ScaleHeight,
          ClientSize.Width,
          Math.Max(
            1,
            ClientSize.Height -
            ScaleHeight));

      e.Graphics.FillRectangle(
        SystemBrushes.Control,
        scale);
      using (var background =
        new SolidBrush(
          Theme.SpectrumBackground))
      {
        e.Graphics.FillRectangle(
          background,
          body);
      }

      DrawScale(
        e.Graphics,
        scale);

      if (hasRows)
        DrawWaterfall(
          e.Graphics,
          body);

      DrawTrackMarkers(
        e.Graphics,
        body);
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
      int firstCount =
        HistoryRows - writeRow;
      int secondCount =
        writeRow;

      if (firstCount > 0)
      {
        Rectangle source =
          new(
            0,
            writeRow,
            waterfall.Width,
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
            0,
            0,
            waterfall.Width,
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
      Rectangle scale)
    {
      const int stepHz = 250;
      int first =
        (int)Math.Ceiling(
          minFrequencyHz /
          stepHz) *
        stepHz;

      using var gridPen =
        new Pen(
          Theme.SpectrumGrid);
      using var textBrush =
        new SolidBrush(
          SystemColors.ControlText);

      for (int hz = first;
           hz <= maxFrequencyHz;
           hz += stepHz)
      {
        float x =
          FrequencyToX(hz);
        g.DrawLine(
          gridPen,
          x,
          scale.Bottom - 8,
          x,
          scale.Bottom);

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
          Theme.SpectrumTrace,
          1.5f);
      using var selectedPen =
        new Pen(
          Theme.SpectrumPeak,
          2.5f);
      using var ambiguousPen =
        new Pen(
          Theme.SpectrumPeak,
          1.5f)
        {
          DashStyle =
            DashStyle.Dash
        };

      foreach (CwSignalTrack track
        in tracks)
      {
        if (track.FrequencyHz <
              minFrequencyHz ||
            track.FrequencyHz >
              maxFrequencyHz)
          continue;

        CwConsoleLaneIdentity identity =
          CwConsolePresentation.Identity(
            track);

        Pen pen =
          selectedIdentity.HasValue &&
          identity ==
            selectedIdentity.Value
            ? selectedPen
            : track.Ambiguous
              ? ambiguousPen
              : normalPen;

        float x =
          FrequencyToX(
            track.FrequencyHz);

        g.DrawLine(
          pen,
          x,
          body.Top,
          x,
          body.Bottom);

        string lane =
          $"#{track.Id}";
        using var backBrush =
          new SolidBrush(
            Color.FromArgb(
              180,
              Theme.SpectrumBackground));
        using var textBrush =
          new SolidBrush(
            pen.Color);

        SizeF size =
          g.MeasureString(
            lane,
            Font);
        RectangleF labelRect =
          new(
            x + 3,
            body.Top + 3,
            size.Width + 4,
            size.Height + 2);
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
              minFrequencyHz &&
            x.FrequencyHz <=
              maxFrequencyHz)
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
        (maxFrequencyHz -
         minFrequencyHz) /
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
         minFrequencyHz) /
        Math.Max(
          maxFrequencyHz -
          minFrequencyHz,
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

      return minFrequencyHz +
        fraction *
        (maxFrequencyHz -
         minFrequencyHz);
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

      if (level < 0.72)
        return Mix(
          Theme.SpectrumBackground,
          Theme.SpectrumTrace,
          level / 0.72);

      return Mix(
        Theme.SpectrumTrace,
        Theme.SpectrumPeak,
        (level - 0.72) / 0.28);
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
