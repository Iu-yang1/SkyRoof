using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkyRoof
{
  internal sealed class IcomLanSpectrumView : Control
  {
    private const int ScopePoints = 475;
    private const int SplitterHeight = 6;
    private const int MinimumSpectrumHeight = 80;
    private const int MinimumWaterfallHeight = 60;
    private const int AxisHeight = 20;

    private readonly object DataSync = new();
    private int[] Palette =
      BuildPalette(
        IcomScopeWaterfallPalette.Classic);

    private byte[] LatestSamples = new byte[ScopePoints];
    private IcomScopeFrame? LatestFrame;

    private byte[][] WaterfallRows = Array.Empty<byte[]>();
    private int WaterfallHead = -1;
    private bool WaterfallDirty = true;
    private Bitmap? WaterfallBitmap;
    private int[] WaterfallArgb = Array.Empty<int>();
    private int[] WaterfallArgbRow = new int[ScopePoints];
    private bool WaterfallVisible = true;
    private int WaterfallBrightness;
    private int WaterfallContrast = 100;
    private IcomScopeWaterfallPalette WaterfallPalette =
      IcomScopeWaterfallPalette.Classic;

    private readonly byte[] PeakSamples =
      new byte[ScopePoints];
    private bool PeakEnabled;
    private bool PeakValid;
    private bool HoldEnabled;

    private double SpectrumFraction = 0.36;
    private bool SplitterDragging;
    private bool PointerInside;
    private Point PointerLocation = new(-1, -1);

    private long ReceiveFrequencyHz;
    private Slicer.Mode? ReceiveMode;

    internal event Action<int>? SpectrumPercentChanged;

    internal IcomLanSpectrumView()
    {
      DoubleBuffered = true;
      BackColor = Color.Black;
      ForeColor = Color.Gainsboro;
      Font = new Font("Segoe UI", 9F);
      ResizeRedraw = true;
      ConfigureHistory(240);
    }

    internal void SetHistoryRows(int rows)
    {
      int clamped =
        Math.Clamp(rows, 40, 800);

      if (WaterfallRows.Length == clamped)
        return;

      ConfigureHistory(clamped);
    }

    internal void SetHold(bool enabled)
    {
      HoldEnabled = enabled;
      Invalidate();
    }

    internal void SetPeakHold(bool enabled)
    {
      if (PeakEnabled == enabled)
        return;

      PeakEnabled = enabled;

      if (enabled)
        ClearPeakHold();

      Invalidate();
    }

    internal void ClearPeakHold()
    {
      lock (DataSync)
      {
        Array.Clear(PeakSamples);
        PeakValid = false;
      }

      Invalidate();
    }

    internal void SetWaterfallVisible(bool visible)
    {
      if (WaterfallVisible == visible)
        return;

      WaterfallVisible = visible;
      Invalidate();
    }

    internal void SetWaterfallDisplay(
      int brightness,
      int contrast,
      IcomScopeWaterfallPalette palette)
    {
      int newBrightness =
        Math.Clamp(brightness, -80, 80);
      int newContrast =
        Math.Clamp(contrast, 25, 250);

      if (WaterfallBrightness == newBrightness &&
          WaterfallContrast == newContrast &&
          WaterfallPalette == palette)
        return;

      lock (DataSync)
      {
        WaterfallBrightness =
          newBrightness;
        WaterfallContrast =
          newContrast;
        WaterfallPalette =
          palette;
        Palette =
          BuildPalette(palette);
        WaterfallDirty = true;
      }

      Invalidate();
    }

    internal void SetSpectrumPercent(int percent)
    {
      SpectrumFraction =
        Math.Clamp(percent, 20, 80) / 100.0;
      Invalidate();
    }

    internal void SetTuningOverlay(
      long receiveFrequencyHz,
      Slicer.Mode? mode)
    {
      if (ReceiveFrequencyHz == receiveFrequencyHz &&
          ReceiveMode == mode)
        return;

      ReceiveFrequencyHz =
        receiveFrequencyHz;
      ReceiveMode =
        mode;
      Invalidate();
    }

    internal void PushFrame(IcomScopeFrame frame)
    {
      if (frame.Samples.Length < ScopePoints)
        return;

      lock (DataSync)
      {
        if (HoldEnabled)
          return;

        LatestFrame = frame;
        Buffer.BlockCopy(
          frame.Samples,
          0,
          LatestSamples,
          0,
          ScopePoints);

        if (PeakEnabled)
        {
          for (int i = 0;
               i < ScopePoints;
               i++)
          {
            if (!PeakValid ||
                frame.Samples[i] >
                  PeakSamples[i])
              PeakSamples[i] =
                frame.Samples[i];
          }

          PeakValid = true;
        }

        // Serial/Remote Utility scope data may arrive as 10-11 divisions.
        // Update the trace for every partial division, but advance waterfall
        // history only after the complete 475-bin sweep has arrived.
        if (frame.SweepComplete &&
            WaterfallRows.Length > 0)
        {
          WaterfallHead =
            WaterfallHead < 0
              ? 0
              : (WaterfallHead -
                 1 +
                 WaterfallRows.Length) %
                WaterfallRows.Length;

          Buffer.BlockCopy(
            frame.Samples,
            0,
            WaterfallRows[WaterfallHead],
            0,
            ScopePoints);

          UpdateWaterfallBitmapRow(
            WaterfallHead);
        }
      }

      Invalidate();
    }

    internal void Clear()
    {
      lock (DataSync)
      {
        LatestFrame = null;
        Array.Clear(LatestSamples);

        foreach (byte[] row in WaterfallRows)
          Array.Clear(row);

        WaterfallHead = -1;
        Array.Clear(PeakSamples);
        PeakValid = false;
        WaterfallDirty = true;
      }

      Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        WaterfallBitmap?.Dispose();
        WaterfallBitmap = null;
      }

      base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);

      Color scopeBack =
        Theme.IsDark
          ? Color.FromArgb(12, 16, 20)
          : Theme.BrandWhite;
      Color textColor =
        Theme.IsDark
          ? Color.Gainsboro
          : Theme.LightInk;

      e.Graphics.Clear(scopeBack);
      e.Graphics.SmoothingMode =
        SmoothingMode.AntiAlias;

      if (ClientSize.Width < 20 ||
          ClientSize.Height < 20)
        return;

      IcomScopeFrame? frame;
      byte[] samples =
        new byte[ScopePoints];
      byte[] peakSamples =
        new byte[ScopePoints];
      bool peakVisible;

      lock (DataSync)
      {
        frame = LatestFrame;
        Buffer.BlockCopy(
          LatestSamples,
          0,
          samples,
          0,
          ScopePoints);
        Buffer.BlockCopy(
          PeakSamples,
          0,
          peakSamples,
          0,
          ScopePoints);
        peakVisible =
          PeakEnabled &&
          PeakValid;
        RebuildWaterfallBitmapIfNeeded();
      }

      GetLayout(
        out Rectangle headerRect,
        out Rectangle spectrumRect,
        out Rectangle splitterRect,
        out Rectangle waterfallRect);

      DrawHeader(
        e.Graphics,
        headerRect,
        frame,
        scopeBack,
        textColor);

      DrawSpectrum(
        e.Graphics,
        spectrumRect,
        samples,
        peakSamples,
        peakVisible,
        frame,
        scopeBack,
        textColor);

      DrawSplitter(
        e.Graphics,
        splitterRect);

      if (waterfallRect.Height > 0 &&
          WaterfallBitmap != null)
      {
        e.Graphics.InterpolationMode =
          InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode =
          PixelOffsetMode.Half;

        DrawWaterfallRing(
          e.Graphics,
          waterfallRect);
      }

      Color borderColor =
        Theme.IsDark
          ? Color.FromArgb(90, 90, 90)
          : Color.FromArgb(170, 190, 202);

      using var borderPen =
        new Pen(borderColor);

      if (spectrumRect.Width > 1 &&
          spectrumRect.Height > 1)
        e.Graphics.DrawRectangle(
          borderPen,
          spectrumRect.Left,
          spectrumRect.Top,
          spectrumRect.Width - 1,
          spectrumRect.Height - 1);

      if (waterfallRect.Width > 1 &&
          waterfallRect.Height > 1)
        e.Graphics.DrawRectangle(
          borderPen,
          waterfallRect.Left,
          waterfallRect.Top,
          waterfallRect.Width - 1,
          waterfallRect.Height - 1);
    }

    protected override void OnMouseDown(
      MouseEventArgs e)
    {
      base.OnMouseDown(e);

      GetLayout(
        out _,
        out _,
        out Rectangle splitter,
        out _);

      if (e.Button == MouseButtons.Left &&
          splitter.Contains(e.Location))
      {
        SplitterDragging = true;
        Capture = true;
        Cursor = Cursors.HSplit;
      }
    }

    protected override void OnMouseUp(
      MouseEventArgs e)
    {
      base.OnMouseUp(e);

      if (!SplitterDragging)
        return;

      SplitterDragging = false;
      Capture = false;

      SpectrumPercentChanged?.Invoke(
        (int)Math.Round(
          SpectrumFraction * 100));

      UpdatePointerCursor(e.Location);
    }

    protected override void OnMouseMove(
      MouseEventArgs e)
    {
      base.OnMouseMove(e);

      PointerInside = true;
      PointerLocation = e.Location;

      if (SplitterDragging)
      {
        int headerHeight =
          Math.Max(24, Font.Height + 8);
        int available =
          Math.Max(
            1,
            ClientSize.Height -
            headerHeight -
            SplitterHeight);

        int desired =
          e.Y - headerHeight;

        int maxSpectrum =
          Math.Max(
            MinimumSpectrumHeight,
            available -
            MinimumWaterfallHeight);

        desired =
          Math.Clamp(
            desired,
            MinimumSpectrumHeight,
            maxSpectrum);

        SpectrumFraction =
          Math.Clamp(
            desired / (double)available,
            0.20,
            0.80);
      }

      UpdatePointerCursor(e.Location);
      Invalidate();
    }

    protected override void OnMouseLeave(
      EventArgs e)
    {
      base.OnMouseLeave(e);

      if (!SplitterDragging)
      {
        PointerInside = false;
        PointerLocation = new Point(-1, -1);
        Cursor = Cursors.Default;
        Invalidate();
      }
    }

    private void UpdatePointerCursor(Point point)
    {
      GetLayout(
        out _,
        out Rectangle spectrum,
        out Rectangle splitter,
        out _);

      if (SplitterDragging ||
          splitter.Contains(point))
      {
        Cursor = Cursors.HSplit;
        return;
      }

      Rectangle plot =
        GetSpectrumPlotRectangle(
          spectrum);

      Cursor =
        plot.Contains(point)
          ? Cursors.Cross
          : Cursors.Default;
    }

    private void GetLayout(
      out Rectangle header,
      out Rectangle spectrum,
      out Rectangle splitter,
      out Rectangle waterfall)
    {
      int headerHeight =
        Math.Max(24, Font.Height + 8);

      if (!WaterfallVisible)
      {
        header =
          new Rectangle(
            0,
            0,
            ClientSize.Width,
            headerHeight);
        spectrum =
          new Rectangle(
            0,
            header.Bottom,
            ClientSize.Width,
            Math.Max(
              0,
              ClientSize.Height -
              headerHeight));
        splitter =
          Rectangle.Empty;
        waterfall =
          Rectangle.Empty;
        return;
      }

      int available =
        Math.Max(
          0,
          ClientSize.Height -
          headerHeight -
          SplitterHeight);

      int spectrumHeight =
        available == 0
          ? 0
          : Math.Clamp(
              (int)Math.Round(
                available *
                SpectrumFraction),
              Math.Min(
                MinimumSpectrumHeight,
                available),
              Math.Max(
                Math.Min(
                  MinimumSpectrumHeight,
                  available),
                available -
                Math.Min(
                  MinimumWaterfallHeight,
                  available)));

      int waterfallHeight =
        Math.Max(
          0,
          available -
          spectrumHeight);

      header =
        new Rectangle(
          0,
          0,
          ClientSize.Width,
          headerHeight);

      spectrum =
        new Rectangle(
          0,
          header.Bottom,
          ClientSize.Width,
          spectrumHeight);

      splitter =
        new Rectangle(
          0,
          spectrum.Bottom,
          ClientSize.Width,
          SplitterHeight);

      waterfall =
        new Rectangle(
          0,
          splitter.Bottom,
          ClientSize.Width,
          waterfallHeight);
    }

    private Rectangle GetSpectrumPlotRectangle(
      Rectangle spectrumBounds)
    {
      int axisHeight =
        spectrumBounds.Height > AxisHeight + 20
          ? AxisHeight
          : 0;

      return new Rectangle(
        spectrumBounds.Left,
        spectrumBounds.Top,
        spectrumBounds.Width,
        Math.Max(
          0,
          spectrumBounds.Height -
          axisHeight));
    }

    private void DrawHeader(
      Graphics graphics,
      Rectangle bounds,
      IcomScopeFrame? frame,
      Color background,
      Color textColor)
    {
      using var back =
        new SolidBrush(background);
      graphics.FillRectangle(
        back,
        bounds);

      string left;
      string right;

      if (frame == null)
      {
        left =
          "Waiting for CI-V 27 00 scope data...";
        right = "";
      }
      else
      {
        left =
          frame.SweepComplete ||
          frame.DivisionMaximum <= 1
            ? $"{frame.ScopeName} · {frame.ModeName}"
            : $"{frame.ScopeName} · {frame.ModeName} · " +
              $"LIVE {frame.DivisionCurrent}/{frame.DivisionMaximum}";

        IcomScopeGeometry geometry =
          frame.Geometry;

        if (geometry.IsValid)
        {
          right =
            frame.Mode ==
              (byte)IcomScopeMode.Center
              ? $"{FormatFrequency(geometry.CenterFrequencyHz)} · " +
                $"{FormatSpan(geometry.SpanHz)}"
              : $"{FormatFrequency(geometry.LowerFrequencyHz)} – " +
                $"{FormatFrequency(geometry.UpperFrequencyHz)}";
        }
        else
        {
          right =
            "frequency geometry unavailable";
        }

        if (frame.OutOfRange)
          right += " · OUT OF RANGE";

        if (HoldEnabled)
          left += " · HOLD";
      }

      var flagsLeft =
        TextFormatFlags.Left |
        TextFormatFlags.VerticalCenter |
        TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix;

      var flagsRight =
        TextFormatFlags.Right |
        TextFormatFlags.VerticalCenter |
        TextFormatFlags.EndEllipsis |
        TextFormatFlags.NoPrefix;

      var leftRect =
        new Rectangle(
          bounds.Left + 6,
          bounds.Top,
          Math.Max(
            1,
            bounds.Width / 2 - 8),
          bounds.Height);

      var rightRect =
        new Rectangle(
          bounds.Left +
          bounds.Width / 2,
          bounds.Top,
          Math.Max(
            1,
            bounds.Width / 2 - 6),
          bounds.Height);

      TextRenderer.DrawText(
        graphics,
        left,
        Font,
        leftRect,
        textColor,
        flagsLeft);

      TextRenderer.DrawText(
        graphics,
        right,
        Font,
        rightRect,
        textColor,
        flagsRight);
    }

    private void DrawSpectrum(
      Graphics graphics,
      Rectangle bounds,
      byte[] samples,
      byte[] peakSamples,
      bool peakVisible,
      IcomScopeFrame? frame,
      Color background,
      Color textColor)
    {
      using var backgroundBrush =
        new SolidBrush(background);
      graphics.FillRectangle(
        backgroundBrush,
        bounds);

      Rectangle plot =
        GetSpectrumPlotRectangle(
          bounds);

      if (plot.Width < 2 ||
          plot.Height < 2)
        return;

      IcomScopeGeometry geometry =
        frame?.Geometry ?? default;

      DrawPassband(
        graphics,
        plot,
        geometry);

      DrawFrequencyGrid(
        graphics,
        bounds,
        plot,
        geometry,
        textColor);

      DrawLevelGrid(
        graphics,
        plot);

      DrawTrace(
        graphics,
        plot,
        samples);

      if (peakVisible)
        DrawPeakTrace(
          graphics,
          plot,
          peakSamples);

      DrawReceiveMarker(
        graphics,
        plot,
        geometry);

      DrawCursorReadout(
        graphics,
        plot,
        geometry,
        textColor);
    }

    private void DrawFrequencyGrid(
      Graphics graphics,
      Rectangle spectrumBounds,
      Rectangle plot,
      IcomScopeGeometry geometry,
      Color textColor)
    {
      int intervals =
        plot.Width >= 900
          ? 5
          : plot.Width >= 600
            ? 4
            : 3;

      Color gridColor =
        Theme.IsDark
          ? Color.FromArgb(65, 105, 112, 118)
          : Color.FromArgb(90, Theme.BrandBlue);

      using var gridPen =
        new Pen(gridColor);

      for (int i = 0;
           i <= intervals;
           i++)
      {
        double fraction =
          i / (double)intervals;

        int x =
          plot.Left +
          (int)Math.Round(
            fraction *
            Math.Max(
              0,
              plot.Width - 1));

        graphics.DrawLine(
          gridPen,
          x,
          plot.Top,
          x,
          plot.Bottom);

        if (!geometry.IsValid ||
            spectrumBounds.Height <=
              plot.Height)
          continue;

        long frequency =
          geometry.FrequencyAtFraction(
            fraction);

        string label =
          FormatAxisFrequency(
            frequency);

        Size textSize =
          TextRenderer.MeasureText(
            graphics,
            label,
            Font,
            Size.Empty,
            TextFormatFlags.NoPadding);

        int labelX =
          Math.Clamp(
            x - textSize.Width / 2,
            spectrumBounds.Left + 2,
            Math.Max(
              spectrumBounds.Left + 2,
              spectrumBounds.Right -
              textSize.Width -
              2));

        var labelBounds =
          new Rectangle(
            labelX,
            plot.Bottom,
            textSize.Width + 2,
            Math.Max(
              1,
              spectrumBounds.Bottom -
              plot.Bottom));

        TextRenderer.DrawText(
          graphics,
          label,
          Font,
          labelBounds,
          textColor,
          TextFormatFlags.HorizontalCenter |
          TextFormatFlags.VerticalCenter |
          TextFormatFlags.NoPrefix |
          TextFormatFlags.NoPadding);
      }
    }

    private static void DrawLevelGrid(
      Graphics graphics,
      Rectangle plot)
    {
      Color gridColor =
        Theme.IsDark
          ? Color.FromArgb(50, 95, 95, 95)
          : Color.FromArgb(45, Theme.BrandPink);

      using var gridPen =
        new Pen(gridColor);

      for (int i = 1;
           i < 4;
           i++)
      {
        int y =
          plot.Top +
          plot.Height * i / 4;

        graphics.DrawLine(
          gridPen,
          plot.Left,
          y,
          plot.Right,
          y);
      }
    }

    private static void DrawTrace(
      Graphics graphics,
      Rectangle plot,
      byte[] samples)
    {
      if (samples.Length < 2)
        return;

      var points =
        new PointF[samples.Length];

      float usableHeight =
        Math.Max(
          1,
          plot.Height - 4);

      for (int i = 0;
           i < samples.Length;
           i++)
      {
        float x =
          plot.Left +
          i *
          (plot.Width - 1f) /
          (samples.Length - 1f);

        float normalized =
          Math.Clamp(
            samples[i] / 160f,
            0f,
            1f);

        float y =
          plot.Bottom -
          2 -
          normalized *
          usableHeight;

        points[i] =
          new PointF(
            x,
            y);
      }

      Color traceColor =
        Theme.IsDark
          ? Theme.BrandBlue
          : Theme.BlueDark;

      using var tracePen =
        new Pen(
          traceColor,
          1.25f);

      graphics.DrawLines(
        tracePen,
        points);
    }

    private static void DrawPeakTrace(
      Graphics graphics,
      Rectangle plot,
      byte[] samples)
    {
      if (samples.Length < 2)
        return;

      var points =
        new PointF[samples.Length];

      float usableHeight =
        Math.Max(
          1,
          plot.Height - 4);

      for (int i = 0;
           i < samples.Length;
           i++)
      {
        float x =
          plot.Left +
          i *
          (plot.Width - 1f) /
          (samples.Length - 1f);

        float normalized =
          Math.Clamp(
            samples[i] / 160f,
            0f,
            1f);

        points[i] =
          new PointF(
            x,
            plot.Bottom -
            2 -
            normalized *
            usableHeight);
      }

      using var pen =
        new Pen(
          Theme.BrandPink,
          1.0f)
        {
          DashStyle =
            DashStyle.Dot
        };

      graphics.DrawLines(
        pen,
        points);
    }

    private void DrawPassband(
      Graphics graphics,
      Rectangle plot,
      IcomScopeGeometry geometry)
    {
      if (!geometry.IsValid ||
          ReceiveFrequencyHz <= 0 ||
          ReceiveMode == null)
        return;

      int bandwidth =
        Slicer.GetBandwidth(
          ReceiveMode.Value);

      int offset =
        Slicer.GetModeOffset(
          ReceiveMode.Value);

      long center =
        ReceiveFrequencyHz +
        offset;

      long low =
        center -
        bandwidth / 2;

      long high =
        low +
        bandwidth;

      double lowFraction =
        geometry.FractionForFrequency(
          low);
      double highFraction =
        geometry.FractionForFrequency(
          high);

      if (double.IsNaN(lowFraction) ||
          double.IsNaN(highFraction) ||
          highFraction < 0 ||
          lowFraction > 1)
        return;

      lowFraction =
        Math.Clamp(
          lowFraction,
          0,
          1);
      highFraction =
        Math.Clamp(
          highFraction,
          0,
          1);

      int left =
        plot.Left +
        (int)Math.Round(
          lowFraction *
          (plot.Width - 1));

      int right =
        plot.Left +
        (int)Math.Round(
          highFraction *
          (plot.Width - 1));

      if (right <= left)
        right = left + 1;

      using var brush =
        new SolidBrush(
          Color.FromArgb(
            Theme.IsDark ? 44 : 54,
            Theme.BrandPink));

      graphics.FillRectangle(
        brush,
        Rectangle.FromLTRB(
          left,
          plot.Top,
          Math.Min(
            plot.Right,
            right),
          plot.Bottom));
    }

    private void DrawReceiveMarker(
      Graphics graphics,
      Rectangle plot,
      IcomScopeGeometry geometry)
    {
      if (!geometry.ContainsFrequency(
            ReceiveFrequencyHz))
        return;

      double fraction =
        geometry.FractionForFrequency(
          ReceiveFrequencyHz);

      int x =
        plot.Left +
        (int)Math.Round(
          fraction *
          (plot.Width - 1));

      using var pen =
        new Pen(
          Theme.BrandPink,
          1.4f);

      graphics.DrawLine(
        pen,
        x,
        plot.Top,
        x,
        plot.Bottom);

      var labelBounds =
        new Rectangle(
          Math.Max(
            plot.Left,
            x - 18),
          plot.Top + 2,
          36,
          Font.Height + 4);

      TextRenderer.DrawText(
        graphics,
        "RX",
        Font,
        labelBounds,
        Theme.IsDark
          ? Color.White
          : Theme.PinkDark,
        TextFormatFlags.HorizontalCenter |
        TextFormatFlags.NoPrefix |
        TextFormatFlags.NoPadding);
    }

    private void DrawCursorReadout(
      Graphics graphics,
      Rectangle plot,
      IcomScopeGeometry geometry,
      Color textColor)
    {
      if (!PointerInside ||
          !geometry.IsValid ||
          !plot.Contains(
            PointerLocation))
        return;

      double fraction =
        (PointerLocation.X -
         plot.Left) /
        (double)Math.Max(
          1,
          plot.Width - 1);

      long frequency =
        geometry.FrequencyAtFraction(
          fraction);

      int x =
        Math.Clamp(
          PointerLocation.X,
          plot.Left,
          plot.Right - 1);

      using var cursorPen =
        new Pen(
          Theme.IsDark
            ? Color.FromArgb(185, 235, 235, 235)
            : Theme.BlueDark,
          1f)
        {
          DashStyle =
            DashStyle.Dash
        };

      graphics.DrawLine(
        cursorPen,
        x,
        plot.Top,
        x,
        plot.Bottom);

      string text =
        FormatCursorText(
          frequency);

      Size textSize =
        TextRenderer.MeasureText(
          graphics,
          text,
          Font,
          Size.Empty,
          TextFormatFlags.NoPadding);

      int left =
        Math.Clamp(
          x + 8,
          plot.Left + 3,
          Math.Max(
            plot.Left + 3,
            plot.Right -
            textSize.Width -
            9));

      int top =
        plot.Top + 22;

      var box =
        new Rectangle(
          left,
          top,
          textSize.Width + 8,
          textSize.Height + 6);

      using var boxBrush =
        new SolidBrush(
          Theme.IsDark
            ? Color.FromArgb(
                220,
                24,
                28,
                32)
            : Color.FromArgb(
                238,
                Theme.BrandWhite));

      using var boxPen =
        new Pen(
          Theme.IsDark
            ? Color.Gray
            : Theme.BrandBlue);

      graphics.FillRectangle(
        boxBrush,
        box);

      graphics.DrawRectangle(
        boxPen,
        box);

      TextRenderer.DrawText(
        graphics,
        text,
        Font,
        new Rectangle(
          box.Left + 4,
          box.Top + 3,
          textSize.Width,
          textSize.Height),
        textColor,
        TextFormatFlags.NoPrefix |
        TextFormatFlags.NoPadding);
    }

    private string FormatCursorText(
      long frequencyHz)
    {
      string frequency =
        $"{frequencyHz / 1_000_000.0:0.000000} MHz";

      if (ReceiveFrequencyHz <= 0)
        return frequency;

      long delta =
        frequencyHz -
        ReceiveFrequencyHz;

      string sign =
        delta >= 0
          ? "+"
          : "−";

      double magnitude =
        Math.Abs(
          (double)delta);

      string deltaText =
        magnitude >= 1_000_000
          ? $"{magnitude / 1_000_000.0:0.###} MHz"
          : magnitude >= 1_000
            ? $"{magnitude / 1_000.0:0.###} kHz"
            : $"{magnitude:0} Hz";

      return
        $"{frequency}   Δ {sign}{deltaText}";
    }

    private static void DrawSplitter(
      Graphics graphics,
      Rectangle bounds)
    {
      if (bounds.Height <= 0)
        return;

      Color back =
        Theme.IsDark
          ? Color.FromArgb(36, 42, 48)
          : Theme.BlueWash;

      Color line =
        Theme.IsDark
          ? Color.FromArgb(105, 115, 125)
          : Theme.BlueDark;

      using var brush =
        new SolidBrush(back);
      using var pen =
        new Pen(line);

      graphics.FillRectangle(
        brush,
        bounds);

      int y =
        bounds.Top +
        bounds.Height / 2;

      graphics.DrawLine(
        pen,
        bounds.Left,
        y,
        bounds.Right,
        y);
    }

    private void ConfigureHistory(int rows)
    {
      lock (DataSync)
      {
        WaterfallRows =
          new byte[rows][];

        for (int i = 0;
             i < rows;
             i++)
          WaterfallRows[i] =
            new byte[ScopePoints];

        WaterfallHead = -1;
        WaterfallDirty = true;

        WaterfallBitmap?.Dispose();
        WaterfallBitmap =
          new Bitmap(
            ScopePoints,
            rows,
            PixelFormat.Format32bppArgb);

        WaterfallArgb =
          new int[
            ScopePoints *
            rows];
        WaterfallArgbRow =
          new int[ScopePoints];
      }

      Invalidate();
    }

    private void UpdateWaterfallBitmapRow(
      int rowIndex)
    {
      if (WaterfallBitmap == null ||
          rowIndex < 0 ||
          rowIndex >= WaterfallRows.Length)
        return;

      byte[] row =
        WaterfallRows[rowIndex];

      for (int x = 0;
           x < ScopePoints;
           x++)
        WaterfallArgbRow[x] =
          Palette[
            MapWaterfallLevel(
              row[x],
              WaterfallBrightness,
              WaterfallContrast)];

      Rectangle rect =
        new(
          0,
          rowIndex,
          ScopePoints,
          1);

      BitmapData data =
        WaterfallBitmap.LockBits(
          rect,
          ImageLockMode.WriteOnly,
          PixelFormat.Format32bppArgb);

      try
      {
        Marshal.Copy(
          WaterfallArgbRow,
          0,
          data.Scan0,
          ScopePoints);
      }
      finally
      {
        WaterfallBitmap.UnlockBits(
          data);
      }
    }

    private void RebuildWaterfallBitmapIfNeeded()
    {
      if (!WaterfallDirty ||
          WaterfallBitmap == null ||
          WaterfallRows.Length == 0)
        return;

      int index = 0;

      for (int y = 0;
           y < WaterfallRows.Length;
           y++)
      {
        byte[] row =
          WaterfallRows[y];

        for (int x = 0;
             x < ScopePoints;
             x++)
          WaterfallArgb[index++] =
            Palette[
              MapWaterfallLevel(
                row[x],
                WaterfallBrightness,
                WaterfallContrast)];
      }

      Rectangle rect =
        new(
          0,
          0,
          WaterfallBitmap.Width,
          WaterfallBitmap.Height);

      BitmapData data =
        WaterfallBitmap.LockBits(
          rect,
          ImageLockMode.WriteOnly,
          PixelFormat.Format32bppArgb);

      try
      {
        if (data.Stride ==
            ScopePoints * 4)
        {
          Marshal.Copy(
            WaterfallArgb,
            0,
            data.Scan0,
            WaterfallArgb.Length);
        }
        else
        {
          for (int y = 0;
               y < WaterfallRows.Length;
               y++)
          {
            Marshal.Copy(
              WaterfallArgb,
              y * ScopePoints,
              data.Scan0 +
              y * data.Stride,
              ScopePoints);
          }
        }
      }
      finally
      {
        WaterfallBitmap.UnlockBits(
          data);
      }

      WaterfallDirty = false;
    }

    private void DrawWaterfallRing(
      Graphics graphics,
      Rectangle destination)
    {
      if (WaterfallBitmap == null ||
          WaterfallRows.Length == 0)
        return;

      if (WaterfallHead < 0)
      {
        graphics.DrawImage(
          WaterfallBitmap,
          destination,
          new Rectangle(
            0,
            0,
            WaterfallBitmap.Width,
            WaterfallBitmap.Height),
          GraphicsUnit.Pixel);
        return;
      }

      int rows =
        WaterfallRows.Length;
      int firstRows =
        rows -
        WaterfallHead;

      int firstHeight =
        (int)Math.Round(
          destination.Height *
          firstRows /
          (double)rows);

      firstHeight =
        Math.Clamp(
          firstHeight,
          0,
          destination.Height);

      if (firstRows > 0 &&
          firstHeight > 0)
      {
        graphics.DrawImage(
          WaterfallBitmap,
          new Rectangle(
            destination.Left,
            destination.Top,
            destination.Width,
            firstHeight),
          new Rectangle(
            0,
            WaterfallHead,
            WaterfallBitmap.Width,
            firstRows),
          GraphicsUnit.Pixel);
      }

      int secondRows =
        WaterfallHead;
      int secondHeight =
        destination.Height -
        firstHeight;

      if (secondRows > 0 &&
          secondHeight > 0)
      {
        graphics.DrawImage(
          WaterfallBitmap,
          new Rectangle(
            destination.Left,
            destination.Top +
            firstHeight,
            destination.Width,
            secondHeight),
          new Rectangle(
            0,
            0,
            WaterfallBitmap.Width,
            secondRows),
          GraphicsUnit.Pixel);
      }
    }

    internal static int MapWaterfallLevel(
      int level,
      int brightness,
      int contrast)
    {
      double mapped =
        (level - 80) *
        Math.Clamp(
          contrast,
          25,
          250) /
        100.0 +
        80 +
        Math.Clamp(
          brightness,
          -80,
          80);

      return Math.Clamp(
        (int)Math.Round(mapped),
        0,
        160);
    }

    private static int[] BuildPalette(
      IcomScopeWaterfallPalette paletteKind)
    {
      var palette =
        new int[161];

      for (int i = 0;
           i < palette.Length;
           i++)
      {
        double t =
          i / 160.0;
        Color color =
          paletteKind switch
          {
            IcomScopeWaterfallPalette.Grayscale =>
              GrayscaleColor(t),
            IcomScopeWaterfallPalette.Blue =>
              BlueColor(t),
            IcomScopeWaterfallPalette.Heat =>
              HeatColor(t),
            _ =>
              ClassicColor(t)
          };

        palette[i] =
          color.ToArgb();
      }

      return palette;
    }

    private static Color ClassicColor(double t)
    {
      if (t < 0.25)
      {
        double u =
          t / 0.25;
        return Color.FromArgb(
          255,
          0,
          0,
          (int)Math.Round(
            25 +
            180 * u));
      }

      if (t < 0.5)
      {
        double u =
          (t - 0.25) /
          0.25;
        return Color.FromArgb(
          255,
          0,
          (int)Math.Round(
            210 * u),
          255);
      }

      if (t < 0.75)
      {
        double u =
          (t - 0.5) /
          0.25;
        return Color.FromArgb(
          255,
          (int)Math.Round(
            255 * u),
          255,
          (int)Math.Round(
            255 *
            (1 - u)));
      }

      double last =
        (t - 0.75) /
        0.25;
      return Color.FromArgb(
        255,
        255,
        255,
        (int)Math.Round(
          255 * last));
    }

    private static Color GrayscaleColor(double t)
    {
      int value =
        (int)Math.Round(
          255 * t);

      return Color.FromArgb(
        255,
        value,
        value,
        value);
    }

    private static Color BlueColor(double t)
    {
      if (t < 0.65)
      {
        double u =
          t / 0.65;
        return Color.FromArgb(
          255,
          0,
          (int)Math.Round(
            190 * u),
          (int)Math.Round(
            40 +
            215 * u));
      }

      double v =
        (t - 0.65) /
        0.35;

      return Color.FromArgb(
        255,
        (int)Math.Round(
          255 * v),
        (int)Math.Round(
          190 +
          65 * v),
        255);
    }

    private static Color HeatColor(double t)
    {
      if (t < 0.5)
      {
        double u =
          t / 0.5;
        return Color.FromArgb(
          255,
          (int)Math.Round(
            255 * u),
          0,
          0);
      }

      double v =
        (t - 0.5) /
        0.5;

      return Color.FromArgb(
        255,
        255,
        (int)Math.Round(
          255 * v),
        (int)Math.Round(
          255 *
          Math.Max(
            0,
            (v - 0.75) /
            0.25)));
    }

    private static string FormatAxisFrequency(
      long hz)
    {
      return hz > 0
        ? $"{hz / 1_000_000.0:0.000000}"
        : "—";
    }

    private static string FormatFrequency(
      long hz)
    {
      return hz > 0
        ? $"{hz / 1_000_000.0:0.000000} MHz"
        : "frequency unknown";
    }

    private static string FormatSpan(
      long hz)
    {
      if (hz <= 0)
        return "span unknown";

      return hz >= 1_000_000
        ? $"Span {hz / 1_000_000.0:0.###} MHz"
        : $"Span {hz / 1_000.0:0.###} kHz";
    }
  }
}
