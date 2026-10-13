using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof
{
  /// <summary>
  /// Proportionally zooms an existing WinForms design surface without reflow,
  /// wrap, scroll bars, or cumulative rounding. All coordinates are calculated
  /// from an immutable design-time snapshot, including nested font sizes.
  /// </summary>
  internal sealed class UniformZoomLayout : IDisposable
  {
    private readonly Control design;
    private readonly Size designSize;
    private readonly List<Snapshot> snapshots = new();
    private float zoom = 1f;
    private bool disposed;

    internal UniformZoomLayout(Control design, Size designSize)
    {
      this.design = design;
      this.designSize = designSize;
      Capture(design);
    }

    internal static float GetScale(Size design, Size available)
    {
      if (design.Width <= 0 || design.Height <= 0 ||
          available.Width <= 0 || available.Height <= 0)
        return 1f;
      return Math.Max(0.10f,
        Math.Min((float)available.Width / design.Width,
                 (float)available.Height / design.Height));
    }

    internal float Zoom => zoom;

    private void Capture(Control control)
    {
      snapshots.Add(new Snapshot(control));
      foreach (Control child in control.Controls)
        Capture(child);
    }

    internal void Fit(Size available)
    {
      if (disposed || design.IsDisposed) return;
      float next = GetScale(designSize, available);
      if (Math.Abs(zoom - next) < 0.002f) return;
      zoom = next;
      Apply(next);
    }

    private void Apply(float factor)
    {
      // Suspend in parent-first order, resume in child-first order. Use the
      // original pixel geometry each time, never a ratio of the prior resize.
      foreach (var item in snapshots)
        if (!item.Control.IsDisposed) item.Control.SuspendLayout();

      try
      {
        foreach (var item in snapshots)
          if (!item.Control.IsDisposed)
            item.Scale(factor);

        design.Bounds = new Rectangle(
          design.Left, design.Top,
          Math.Max(1, (int)Math.Round(designSize.Width * factor)),
          Math.Max(1, (int)Math.Round(designSize.Height * factor)));
      }
      finally
      {
        for (int i = snapshots.Count - 1; i >= 0; i--)
          if (!snapshots[i].Control.IsDisposed)
            snapshots[i].Control.ResumeLayout(false);
      }
      design.Invalidate(true);
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      foreach (var snapshot in snapshots) snapshot.Dispose();
      snapshots.Clear();
    }

    private sealed class Snapshot : IDisposable
    {
      internal readonly Control Control;
      private readonly Rectangle bounds;
      private readonly Padding margin;
      private readonly Padding padding;
      private readonly Size minSize;
      private readonly Font originalFont;
      private readonly bool ownsOriginalFont;
      private Font? activeFont;
      private readonly (RowStyle style, float height)[] rows;
      private readonly (ColumnStyle style, float width)[] columns;

      internal Snapshot(Control control)
      {
        Control = control;
        bounds = control.Bounds;
        margin = control.Margin;
        padding = control.Padding;
        minSize = control.MinimumSize;
        originalFont = (Font)control.Font.Clone();
        ownsOriginalFont = true;

        if (control is TableLayoutPanel table)
        {
          rows = table.RowStyles.Cast<RowStyle>()
            .Select(s => (s, s.Height)).ToArray();
          columns = table.ColumnStyles.Cast<ColumnStyle>()
            .Select(s => (s, s.Width)).ToArray();
        }
        else
        {
          rows = Array.Empty<(RowStyle, float)>();
          columns = Array.Empty<(ColumnStyle, float)>();
        }
      }

      internal void Scale(float factor)
      {
        if (Control.IsDisposed) return;

        Control.Margin = Multiply(margin, factor);
        Control.Padding = Multiply(padding, factor);

        if (Control is TableLayoutPanel)
        {
          foreach (var (style, baseline) in rows)
            if (style.SizeType == SizeType.Absolute)
              style.Height = Math.Max(1, baseline * factor);
          foreach (var (style, baseline) in columns)
            if (style.SizeType == SizeType.Absolute)
              style.Width = Math.Max(1, baseline * factor);
        }

        var font = new Font(originalFont.FontFamily,
          Math.Max(4f, originalFont.SizeInPoints * factor),
          originalFont.Style, GraphicsUnit.Point);
        Font? old = activeFont;
        activeFont = font;
        Control.Font = font;
        old?.Dispose();

        Control.MinimumSize = Multiply(minSize, factor);

        // TableLayoutPanel and docking must keep ownership of their cell
        // positions; overriding those bounds fights WinForms layout.
        if (Control.Parent is not TableLayoutPanel &&
            Control.Dock == DockStyle.None)
          Control.Bounds = ScaleBounds(bounds, factor);

        // For FlowLayoutPanel children preserve their original control sizes
        // but keep their order. No wrapping or scrolling is introduced here.
        if (Control.Parent is FlowLayoutPanel && Control.Dock == DockStyle.None)
          Control.Size = Multiply(bounds.Size, factor);
      }

      private static Rectangle ScaleBounds(Rectangle r, float f) =>
        new((int)Math.Round(r.X * f), (int)Math.Round(r.Y * f),
            Math.Max(1, (int)Math.Round(r.Width * f)),
            Math.Max(1, (int)Math.Round(r.Height * f)));

      private static Size Multiply(Size s, float f) =>
        new((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));

      private static Padding Multiply(Padding p, float f) =>
        new((int)Math.Round(p.Left * f), (int)Math.Round(p.Top * f),
            (int)Math.Round(p.Right * f), (int)Math.Round(p.Bottom * f));

      public void Dispose()
      {
        if (ownsOriginalFont) originalFont.Dispose();
        activeFont?.Dispose();
      }
    }
  }
}
