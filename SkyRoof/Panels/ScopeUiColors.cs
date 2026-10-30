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

  internal static class IcomScopeToolbarLayout
  {
    /// <summary>
    /// Keep two row groups intact and scale the entire toolbar rather than
    /// wrapping controls, introducing scrollbars or hiding CAT operations.
    /// </summary>
    internal static float CalculateScale(int available, int required)
    {
      if (required <= 0 || available <= 0) return 1;
      return Math.Clamp(available / (float)required, .55f, 1f);
    }
  }
}
