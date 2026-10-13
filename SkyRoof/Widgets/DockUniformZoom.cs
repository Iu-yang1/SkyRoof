using System.Drawing;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// Keeps a docked window's original design coordinates. The viewport is the
  /// only Dock=Fill control; the contents never wrap, reflow or scroll when a
  /// dock tab shrinks. Native owner-drawn surfaces receive their actual scaled
  /// ClientSize, so their existing mouse hit tests remain client-relative.
  /// </summary>
  internal sealed class DockUniformZoom : IDisposable
  {
    private readonly Panel viewport;
    private readonly UniformZoomLayout zoom;
    private bool disposed;

    private DockUniformZoom(DockContent owner, Control design, Size designSize)
    {
      owner.AutoScaleMode = AutoScaleMode.None;
      owner.AutoScroll = false; // DockContent must not add an overflow scrollbar.
      owner.Controls.Remove(design);
      design.Dock = DockStyle.None;
      design.AutoSize = false;
      design.Location = Point.Empty;
      design.Size = designSize;

      viewport = new Panel
      {
        Dock = DockStyle.Fill,
        AutoScroll = false,
        Margin = Padding.Empty,
        Padding = Padding.Empty
      };
      viewport.Controls.Add(design);
      owner.Controls.Add(viewport);
      PerformLayoutTree(design);
      zoom = new UniformZoomLayout(design, designSize);
      viewport.ClientSizeChanged += ResizeViewport;
      owner.Disposed += (_, _) => Dispose();
      ResizeViewport(null, EventArgs.Empty);
    }

    internal static DockUniformZoom Attach(DockContent owner, Control design, Size designSize)
      => new(owner, design, designSize);

    internal Panel Viewport => viewport;
    internal float Scale => zoom.Zoom;

    private void ResizeViewport(object? sender, EventArgs e)
    {
      if (disposed || viewport.IsDisposed) return;
      zoom.Fit(viewport.ClientSize);
    }

    private static void PerformLayoutTree(Control root)
    {
      root.PerformLayout();
      foreach (Control child in root.Controls)
        PerformLayoutTree(child);
    }

    /// <summary>
    /// Transfer an existing flow toolbar to absolute baseline coordinates.
    /// Measure each child once at its design font and pack rows ONLY here.
    /// Subsequent dock resizing cannot create flow rewrap or scrollbars,
    /// including when a button's AutoSize would otherwise fight font zoom.
    /// </summary>
    internal static Panel FreezeToolbar(FlowLayoutPanel flow, int designWidth)
    {
      if (designWidth <= 0) throw new ArgumentOutOfRangeException(nameof(designWidth));
      flow.Dock = DockStyle.None;
      flow.AutoSize = false;
      flow.AutoScroll = false;
      flow.WrapContents = false;
      flow.Size = new Size(designWidth, 600);
      flow.PerformLayout();

      Control[] children = flow.Controls.Cast<Control>().ToArray();
      var fixedToolbar = new Panel
      {
        Dock = DockStyle.Fill,
        AutoSize = false,
        AutoScroll = false,
        Margin = flow.Margin,
        Padding = flow.Padding
      };

      int x = flow.Padding.Left;
      int y = flow.Padding.Top;
      int rowHeight = 0;
      int contentRight = designWidth - flow.Padding.Right;

      foreach (Control child in children)
      {
        // Preserve the design-size AutoSize measurement before disabling it.
        // Otherwise buttons regrow after UniformZoomLayout scales their font.
        Size size = child.Size;
        int requiredWidth = child.Margin.Left + size.Width + child.Margin.Right;
        int requiredHeight = child.Margin.Top + size.Height + child.Margin.Bottom;
        if (x > flow.Padding.Left && x + requiredWidth > contentRight)
        {
          x = flow.Padding.Left;
          y += rowHeight;
          rowHeight = 0;
        }

        flow.Controls.Remove(child);
        child.AutoSize = false;
        child.Dock = DockStyle.None;
        child.Location = new Point(x + child.Margin.Left, y + child.Margin.Top);
        child.Size = size;
        fixedToolbar.Controls.Add(child);

        x += requiredWidth;
        rowHeight = Math.Max(rowHeight, requiredHeight);
      }

      fixedToolbar.Height = Math.Max(1, y + rowHeight + flow.Padding.Bottom);
      return fixedToolbar;
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      viewport.ClientSizeChanged -= ResizeViewport;
      zoom.Dispose();
    }
  }
}
