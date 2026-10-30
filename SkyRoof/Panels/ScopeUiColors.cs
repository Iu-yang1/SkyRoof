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

  internal enum ScopeToolbarDensity
  {
    Standard,
    Compact,
    Restricted,
    Minimal
  }

  internal static class IcomScopeToolbarLayout
  {
    /// <summary>
    /// Evaluate complete, measured control groups in physical client pixels.
    /// Never reduce fonts, split a caption from its selector, wrap the rows,
    /// or claim a mode fits unless BOTH rows fit the dock width.
    /// </summary>
    internal static ScopeToolbarDensity Pick(
      int available,
      int standardRadio, int standardDisplay,
      int compactRadio, int compactDisplay,
      int restrictedRadio, int restrictedDisplay,
      int minimalRadio, int minimalDisplay)
    {
      if (available > 0 &&
          standardRadio <= available && standardDisplay <= available)
        return ScopeToolbarDensity.Standard;
      if (available > 0 &&
          compactRadio <= available && compactDisplay <= available)
        return ScopeToolbarDensity.Compact;
      if (available > 0 &&
          restrictedRadio <= available && restrictedDisplay <= available)
        return ScopeToolbarDensity.Restricted;
      return ScopeToolbarDensity.Minimal;
    }

    internal static bool Fits(int available, params int[] widths) =>
      available > 0 && widths.All(w => w <= available);

    /// <summary>
    /// Check the actual bounds after WinForms has completed a layout pass.
    /// This detects clipping separately from the pure width-budget decision.
    /// </summary>
    internal static bool IsWithin(int x, int width, int clientWidth) =>
      x >= 0 && width > 0 && x + width <= clientWidth;
  }

}
