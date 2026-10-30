using System;
using System.Drawing;

namespace SkyRoof
{
  /// <summary>
  /// Operator toolbar colors from the existing SkyRoof startup theme.
  /// The oscilloscope raster stays dark and is not themed here.
  /// </summary>
  internal sealed class ScopeUiColors
  {
    internal Color Background { get; init; }
    internal Color Surface { get; init; }
    internal Color Text { get; init; }
    internal Color Muted { get; init; }
    internal Color Accent { get; init; }
    internal Color SelectedText { get; init; }
    internal Color Border { get; init; }

    internal static ScopeUiColors Current =>
      For(Theme.Mode, Theme.IsDark);

    internal static ScopeUiColors For(ThemeMode mode, bool systemDark = false)
    {
      if (mode is ThemeMode.GitHubLight or ThemeMode.GitHubDark)
      {
        GitHubPalette p = mode == ThemeMode.GitHubDark
          ? GitHubThemeColors.Dark : GitHubThemeColors.Light;
        return new ScopeUiColors
        {
          Background = p.CanvasDefault,
          Surface = p.CanvasSubtle,
          Text = p.TextPrimary,
          Muted = p.TextSecondary,
          Accent = p.AccentEmphasis,
          SelectedText = Color.White,
          Border = p.BorderDefault
        };
      }
      bool dark = mode == ThemeMode.Dark ||
        mode == ThemeMode.System && systemDark;
      return dark ? new ScopeUiColors
      {
        Background = Color.FromArgb(27, 32, 40),
        Surface = Color.FromArgb(38, 44, 53),
        Text = Color.FromArgb(228, 234, 241),
        Muted = Color.FromArgb(163, 178, 194),
        Accent = Color.FromArgb(52, 126, 206),
        SelectedText = Color.White,
        Border = Color.FromArgb(69, 80, 91)
      } : new ScopeUiColors
      {
        Background = Color.FromArgb(250, 252, 254),
        Surface = Color.FromArgb(238, 246, 251),
        Text = Theme.LightInk,
        Muted = Color.FromArgb(91, 111, 129),
        Accent = Theme.BlueDark,
        SelectedText = Color.White,
        Border = Color.FromArgb(197, 211, 224)
      };
    }
  }

  internal static class IcomScopeStatusSummary
  {
    /// <summary>
    /// Compact text for the visible status strip. It never claims that a
    /// queued CAT write has been confirmed just because its send succeeded.
    /// Full packet counters remain in the existing diagnostic report.
    /// </summary>
    internal static string Format(
      string transport, double waterfallFps, string controlPath,
      bool readOnly, bool backendOnline, bool readbackConfirmed,
      int pending, int timedOut)
    {
      string state =
        readOnly ? "只读" :
        !backendOnline ? "控制后端未连接" :
        timedOut > 0 ? "操作超时" :
        pending > 0 ? "等待电台确认" :
        readbackConfirmed ? "已同步" : "待同步";
      return $"{transport} · {Math.Max(0, waterfallFps):0.0} 帧/s · " +
        $"控制 {controlPath}（{state}）" +
        (pending > 0 ? $" · 待确认 {pending}" : "");
    }
  }

  internal static class IcomScopeToolbarLayout
  {
    /// <summary>
    /// Measure actual rendered text instead of retaining default WinForms
    /// Label.Width (100px) and Button.Width (75px) from initialization.
    /// These invisible gaps caused the previous four-row regression.
    /// </summary>
    internal static int PreferredWidth(System.Windows.Forms.Control control)
    {
      var font = control.Font;
      int Measure(string text) => System.Windows.Forms.TextRenderer.MeasureText(
        text, font, Size.Empty,
        System.Windows.Forms.TextFormatFlags.NoPadding).Width;

      if (control is System.Windows.Forms.Label label)
        return Math.Max(28, Measure(label.Text) + 7);
      if (control is System.Windows.Forms.Button button)
        return Math.Max(48, Measure(button.Text) + 18);
      if (control is System.Windows.Forms.ComboBox combo)
        return Math.Max(55, combo.Items.Cast<object>()
          .Select(value => Measure(value.ToString() ?? string.Empty))
          .DefaultIfEmpty(0).Max() + 27);
      if (control is System.Windows.Forms.NumericUpDown)
        return 76;
      return Math.Max(32, control.Width);
    }

    internal static int RequiredWidth(
      IEnumerable<System.Windows.Forms.Control> controls,
      System.Windows.Forms.Control? ignoredWhenHidden = null)
    {
      return controls.Where(c => c.Visible)
        .Sum(c => c.Width + c.Margin.Horizontal);
    }

    // Deduct overflow against the full caption width, not its already
    // shortened value, so repetitive refreshes cannot oscillate.
    internal static int GeometryWidth(
      int available, int requiredAtFullWidth,
      int preferred = 104, int minimum = 60)
    {
      if (available <= 0) return preferred;
      return Math.Max(minimum,
        preferred - Math.Max(0, requiredAtFullWidth - available));
    }
  }
}
