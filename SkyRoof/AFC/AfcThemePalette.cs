using System.Drawing;

namespace SkyRoof.AFC;

/// <summary>
/// SkyRoof's AFC-specific semantic colors. Derived from the startup Theme,
/// not a hardcoded dark-only palette. Supports classic, GitHub and System
/// light/dark modes without modifying the application's global theme.
/// </summary>
internal sealed class AfcThemePalette
{
  public Color Background { get; init; }
  public Color Surface { get; init; }
  public Color Plot { get; init; }
  public Color Border { get; init; }
  public Color Text { get; init; }
  public Color Muted { get; init; }
  public Color Accent { get; init; }
  public Color SelectedText { get; init; }
  public Color Trace { get; init; }
  public Color Good { get; init; }
  public Color Warning { get; init; }
  public bool Dark { get; init; }

  public static AfcThemePalette Current =>
    For(Theme.Mode, Theme.IsDark);

  internal static AfcThemePalette For(ThemeMode mode, bool systemDark = false)
  {
    bool dark = mode is ThemeMode.Dark or ThemeMode.GitHubDark ||
      mode == ThemeMode.System && systemDark;
    if (mode is ThemeMode.GitHubDark or ThemeMode.GitHubLight)
    {
      var c = dark ? GitHubThemeColors.Dark : GitHubThemeColors.Light;
      return new()
      {
        Background = c.CanvasDefault,
        Surface = c.CanvasSubtle,
        Plot = c.CanvasInset,
        Border = c.BorderDefault,
        Text = c.TextPrimary,
        Muted = c.TextSecondary,
        Accent = c.AccentEmphasis,
        SelectedText = Color.White,
        Trace = c.Accent,
        Good = c.Success,
        Warning = c.Attention,
        Dark = dark
      };
    }
    return dark ? new()
    {
      Background = Color.FromArgb(28, 32, 39),
      Surface = Color.FromArgb(38, 44, 52),
      Plot = Color.FromArgb(19, 24, 31),
      Border = Color.FromArgb(69, 80, 91),
      Text = Color.FromArgb(235, 241, 246),
      Muted = Color.FromArgb(170, 185, 198),
      Accent = Color.FromArgb(48, 130, 226),
      SelectedText = Color.White,
      Trace = Color.FromArgb(72, 174, 244),
      Good = Color.FromArgb(82, 200, 131),
      Warning = Color.FromArgb(239, 180, 70),
      Dark = true
    } : new()
    {
      Background = Color.FromArgb(248, 251, 254),
      Surface = Color.FromArgb(239, 247, 252),
      Plot = Color.White,
      Border = Color.FromArgb(203, 218, 230),
      Text = Color.FromArgb(32, 56, 75),
      Muted = Color.FromArgb(89, 110, 127),
      Accent = Theme.BlueDark,
      SelectedText = Color.White,
      Trace = Color.FromArgb(30, 142, 209),
      Good = Color.FromArgb(18, 124, 84),
      Warning = Color.FromArgb(147, 99, 21),
      Dark = false
    };
  }
}
