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
    /// Capture the FlowLayoutPanel's original wrapped rows once at design
    /// width, then replace them with fixed pixel positions. Runtime resize
    /// must not let FlowLayoutPanel wrap a toolbar differently.
    /// </summary>
    internal static Panel FreezeToolbar(FlowLayoutPanel flow, int designWidth)
    {
      flow.Dock = DockStyle.None;
      flow.AutoSize = false;
      flow.AutoScroll = false;
      flow.WrapContents = true; // one-time layout capture only
      flow.Size = new Size(designWidth, 600);
      flow.PerformLayout();

      Control[] children = flow.Controls.Cast<Control>().ToArray();
      Rectangle[] positions = children.Select(child => child.Bounds).ToArray();
      int bottom = positions.Length == 0 ? 1 : positions.Max(r => r.Bottom);
      var fixedToolbar = new Panel
      {
        Dock = DockStyle.Fill,
        AutoScroll = false,
        Margin = flow.Margin,
        Padding = flow.Padding,
        Height = bottom + flow.Padding.Bottom
      };

      for (int i = 0; i < children.Length; i++)
      {
        Control child = children[i];
        flow.Controls.Remove(child);
        fixedToolbar.Controls.Add(child);
        child.Location = positions[i].Location;
      }
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
