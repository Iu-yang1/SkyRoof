using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SkyRoof.IF
{
  /// <summary>
  /// Compact 4-stage receive link strip. Green means the local stage is
  /// moving samples, not that the remote WSJT-X client is decoding.
  /// Requested WASAPI period is not confused with measured latency.
  /// </summary>
  public sealed class IfAfLinkBar : Control
  {
    private IfAfStatus? latest;
    private IfAfAudioRoute route;
    private bool enabled;

    public IfAfLinkBar()
    {
      Size = new Size(333, 25);
      MinimumSize = new Size(270, 25);
      MaximumSize = new Size(380, 25);
      SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer |
        ControlStyles.UserPaint, true);
      Font = new Font("Segoe UI", 8.2F);
      Cursor = Cursors.Hand;
      AccessibleName = "IF-AF-Audio receiver link status";
    }

    public void UpdateLink(bool configured, IfAfAudioRoute activeRoute,
      IfAfStatus? current)
    {
      enabled = configured;
      route = activeRoute;
      latest = current;
      Visible = configured;
      Invalidate();
    }

    private static void DrawStage(Graphics g, Rectangle bounds,
      string label, Color color, Color textColor)
    {
      using var brush = new SolidBrush(color);
      g.FillRectangle(brush, bounds.Left + 4, bounds.Top + 9, 5, 5);
      TextRenderer.DrawText(g, label, SystemFonts.MessageBoxFont,
        new Rectangle(bounds.Left + 12, bounds.Top + 1,
          bounds.Width - 12, bounds.Height - 2),
        textColor, TextFormatFlags.VerticalCenter |
        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      var g = e.Graphics;
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.Clear(BackColor);
      if (!enabled) return;
      var st = latest;
      bool input = st.HasValue && st.Value.InputSamples > 0 &&
        (DateTime.UtcNow - st.Value.LastInputUtc).TotalSeconds <= 2.0;
      bool ddc = input && st!.Value.OutputSamples > 0 &&
        st.Value.OutputSamples <= st.Value.InputSamples;
      bool render = st.HasValue && st.Value.RenderRunning;
      bool driver = route == IfAfAudioRoute.ExistingVirtualCable ||
        (st.HasValue && st.Value.VirtualDeviceStatus.Contains(
          "endpoints detected", StringComparison.OrdinalIgnoreCase));
      Color ok = Color.FromArgb(45, 150, 95);
      Color warn = Color.FromArgb(200, 145, 32);
      Color bad = Color.FromArgb(195, 76, 72);
      int w = Math.Max(34, (Width - 98) / 4);
      string[] stages = { "IF", "DDC", "AF", (route == IfAfAudioRoute.PreferIfAfAudio || route == IfAfAudioRoute.AutoSignedCable) ? "V-MIC" : "VAC" };
      Color[] colors =
      {
        input ? ok : warn,
        ddc ? ok : warn,
        render ? ok : bad,
        render && driver ? ok : warn
      };
      for (int n = 0; n < 4; n++)
      {
        DrawStage(g, new Rectangle(n * w, 0, w, Height - 1),
          stages[n], colors[n], ForeColor);
        if (n < 3)
          TextRenderer.DrawText(g, "›", Font,
            new Rectangle((n + 1) * w - 7, 0, 12, Height),
            ForeColor, TextFormatFlags.VerticalCenter);
      }
      string summary = st.HasValue
        ? $"Q:{st.Value.ProcessingQueueLagMs:F0}ms  P:{st.Value.RequestedRenderPeriodMs}ms"
        : "Not started";
      TextRenderer.DrawText(g, summary, Font,
        new Rectangle(4 * w + 2, 0, Width - 4 * w - 4, Height),
        ForeColor, TextFormatFlags.VerticalCenter |
        TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }
  }
}
