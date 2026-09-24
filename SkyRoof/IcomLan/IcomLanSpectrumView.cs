using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkyRoof
{
  internal sealed class IcomLanSpectrumView : Control
  {
    private const int ScopePoints = 475;

    private readonly object DataSync = new();
    private byte[] LatestSamples = new byte[ScopePoints];
    private IcomScopeFrame? LatestFrame;

    private byte[][] WaterfallRows = Array.Empty<byte[]>();
    private int WaterfallHead = -1;
    private bool WaterfallDirty = true;

    private Bitmap? WaterfallBitmap;
    private int[] WaterfallArgb = Array.Empty<int>();
    private readonly int[] Palette = BuildPalette();

    internal void SetHistoryRows(int rows)
    {
      ConfigureHistory(Math.Clamp(rows, 40, 800));
    }

    internal IcomLanSpectrumView()
    {
      DoubleBuffered = true;
      BackColor = Color.Black;
      ForeColor = Color.Gainsboro;
      Font = new Font("Segoe UI", 9F);
      ResizeRedraw = true;
      ConfigureHistory(240);
    }

    internal void PushFrame(IcomScopeFrame frame)
    {
      if (frame.Samples.Length < ScopePoints) return;

      lock (DataSync)
      {
        LatestFrame = frame;
        Buffer.BlockCopy(frame.Samples, 0, LatestSamples, 0, ScopePoints);

        if (WaterfallRows.Length > 0)
        {
          WaterfallHead = (WaterfallHead + 1) % WaterfallRows.Length;
          Buffer.BlockCopy(
            frame.Samples,
            0,
            WaterfallRows[WaterfallHead],
            0,
            ScopePoints);
          WaterfallDirty = true;
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

      e.Graphics.Clear(BackColor);
      e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

      if (ClientSize.Width < 20 || ClientSize.Height < 20)
        return;

      IcomScopeFrame? frame;
      byte[] samples = new byte[ScopePoints];

      lock (DataSync)
      {
        frame = LatestFrame;
        Buffer.BlockCopy(LatestSamples, 0, samples, 0, ScopePoints);
        RebuildWaterfallBitmapIfNeeded();
      }

      int headerHeight = Math.Max(24, Font.Height + 8);
      int spectrumHeight = Math.Max(80, (ClientSize.Height - headerHeight) * 36 / 100);
      int waterfallTop = headerHeight + spectrumHeight;
      int waterfallHeight = Math.Max(0, ClientSize.Height - waterfallTop);

      var headerRect = new Rectangle(0, 0, ClientSize.Width, headerHeight);
      var spectrumRect = new Rectangle(0, headerHeight, ClientSize.Width, spectrumHeight);
      var waterfallRect = new Rectangle(0, waterfallTop, ClientSize.Width, waterfallHeight);

      DrawHeader(e.Graphics, headerRect, frame);
      DrawSpectrum(e.Graphics, spectrumRect, samples);

      if (waterfallHeight > 0 && WaterfallBitmap != null)
      {
        // The radio provides 475 horizontal bins. Bilinear scaling is much easier to
        // read than nearest-neighbour when the dock panel is 2x-3x wider than that.
        e.Graphics.InterpolationMode = InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(
          WaterfallBitmap,
          waterfallRect,
          new Rectangle(0, 0, WaterfallBitmap.Width, WaterfallBitmap.Height),
          GraphicsUnit.Pixel);
      }

      using var borderPen = new Pen(Color.FromArgb(90, 90, 90));
      e.Graphics.DrawRectangle(
        borderPen,
        0,
        headerHeight,
        Math.Max(0, ClientSize.Width - 1),
        Math.Max(0, spectrumHeight - 1));

      if (waterfallHeight > 0)
      {
        e.Graphics.DrawRectangle(
          borderPen,
          0,
          waterfallTop,
          Math.Max(0, ClientSize.Width - 1),
          Math.Max(0, waterfallHeight - 1));
      }
    }

    private void DrawHeader(Graphics graphics, Rectangle bounds, IcomScopeFrame? frame)
    {
      string left;
      string right;

      if (frame == null)
      {
        left = "Waiting for CI-V 27 00 scope data...";
        right = "";
      }
      else
      {
        left = $"{frame.ScopeName} · {frame.ModeName}";

        if (frame.Mode is 0 or 2)
        {
          string center = FormatFrequency(frame.CenterFrequencyHz);
          string span = FormatSpan(frame.SpanHz);
          right = frame.OutOfRange
            ? $"{center} · {span} · OUT OF RANGE"
            : $"{center} · {span}";
        }
        else
        {
          right =
            $"{FormatFrequency(frame.FrequencyAHz)} – " +
            $"{FormatFrequency(frame.FrequencyBHz)}";
          if (frame.OutOfRange) right += " · OUT OF RANGE";
        }
      }

      using var brush = new SolidBrush(ForeColor);
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

      var leftRect = new Rectangle(
        bounds.Left + 6,
        bounds.Top,
        Math.Max(1, bounds.Width / 2 - 8),
        bounds.Height);
      var rightRect = new Rectangle(
        bounds.Left + bounds.Width / 2,
        bounds.Top,
        Math.Max(1, bounds.Width / 2 - 6),
        bounds.Height);

      TextRenderer.DrawText(graphics, left, Font, leftRect, ForeColor, flagsLeft);
      TextRenderer.DrawText(graphics, right, Font, rightRect, ForeColor, flagsRight);
    }

    private static void DrawSpectrum(Graphics graphics, Rectangle bounds, byte[] samples)
    {
      graphics.FillRectangle(Brushes.Black, bounds);

      using var gridPen = new Pen(Color.FromArgb(55, 90, 90, 90));
      for (int i = 1; i < 5; i++)
      {
        int x = bounds.Left + bounds.Width * i / 5;
        graphics.DrawLine(gridPen, x, bounds.Top, x, bounds.Bottom);
      }

      for (int i = 1; i < 4; i++)
      {
        int y = bounds.Top + bounds.Height * i / 4;
        graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
      }

      if (samples.Length < 2) return;

      var points = new PointF[samples.Length];
      float usableHeight = Math.Max(1, bounds.Height - 4);

      for (int i = 0; i < samples.Length; i++)
      {
        float x =
          bounds.Left +
          i * (bounds.Width - 1f) /
          (samples.Length - 1f);

        float normalized = Math.Clamp(samples[i] / 160f, 0f, 1f);
        float y =
          bounds.Bottom - 2 -
          normalized * usableHeight;

        points[i] = new PointF(x, y);
      }

      using var tracePen = new Pen(Color.Cyan, 1.2f);
      graphics.DrawLines(tracePen, points);
    }

    private void ConfigureHistory(int rows)
    {
      lock (DataSync)
      {
        WaterfallRows = new byte[rows][];
        for (int i = 0; i < rows; i++)
          WaterfallRows[i] = new byte[ScopePoints];

        WaterfallHead = -1;
        WaterfallDirty = true;

        WaterfallBitmap?.Dispose();
        WaterfallBitmap = new Bitmap(
          ScopePoints,
          rows,
          PixelFormat.Format32bppArgb);
        WaterfallArgb = new int[ScopePoints * rows];
      }

      Invalidate();
    }

    private void RebuildWaterfallBitmapIfNeeded()
    {
      if (!WaterfallDirty ||
          WaterfallBitmap == null ||
          WaterfallRows.Length == 0)
        return;

      int rows = WaterfallRows.Length;
      int index = 0;

      for (int y = 0; y < rows; y++)
      {
        int rowIndex =
          WaterfallHead < 0
            ? 0
            : (WaterfallHead - y + rows) % rows;

        byte[] row = WaterfallRows[rowIndex];

        for (int x = 0; x < ScopePoints; x++)
        {
          int level = Math.Clamp((int)row[x], 0, 160);
          WaterfallArgb[index++] = Palette[level];
        }
      }

      Rectangle rect = new(0, 0, WaterfallBitmap.Width, WaterfallBitmap.Height);
      BitmapData data = WaterfallBitmap.LockBits(
        rect,
        ImageLockMode.WriteOnly,
        PixelFormat.Format32bppArgb);

      try
      {
        if (data.Stride == ScopePoints * 4)
        {
          Marshal.Copy(
            WaterfallArgb,
            0,
            data.Scan0,
            WaterfallArgb.Length);
        }
        else
        {
          int rowInts = ScopePoints;
          for (int y = 0; y < rows; y++)
          {
            Marshal.Copy(
              WaterfallArgb,
              y * rowInts,
              data.Scan0 + y * data.Stride,
              rowInts);
          }
        }
      }
      finally
      {
        WaterfallBitmap.UnlockBits(data);
      }

      WaterfallDirty = false;
    }

    private static int[] BuildPalette()
    {
      var palette = new int[161];

      for (int i = 0; i < palette.Length; i++)
      {
        double t = i / 160.0;
        Color color;

        if (t < 0.25)
        {
          double u = t / 0.25;
          color = Color.FromArgb(
            255,
            0,
            0,
            (int)Math.Round(25 + 180 * u));
        }
        else if (t < 0.5)
        {
          double u = (t - 0.25) / 0.25;
          color = Color.FromArgb(
            255,
            0,
            (int)Math.Round(210 * u),
            255);
        }
        else if (t < 0.75)
        {
          double u = (t - 0.5) / 0.25;
          color = Color.FromArgb(
            255,
            (int)Math.Round(255 * u),
            255,
            (int)Math.Round(255 * (1 - u)));
        }
        else
        {
          double u = (t - 0.75) / 0.25;
          color = Color.FromArgb(
            255,
            255,
            255,
            (int)Math.Round(255 * u));
        }

        palette[i] = color.ToArgb();
      }

      return palette;
    }

    private static string FormatFrequency(long hz)
    {
      if (hz <= 0) return "frequency unknown";
      return $"{hz / 1_000_000.0:0.000000} MHz";
    }

    private static string FormatSpan(long hz)
    {
      if (hz <= 0) return "span unknown";
      return hz >= 1_000_000
        ? $"Span {hz / 1_000_000.0:0.###} MHz"
        : $"Span {hz / 1_000.0:0.###} kHz";
    }
  }
}
