using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  internal sealed class GitHubLightDockTheme :
    VS2015LightTheme
  {
    internal GitHubLightDockTheme()
    {
      GitHubDockPalette.Apply(
        ColorPalette,
        GitHubThemeColors.Light);
    }
  }

  internal sealed class GitHubDarkDockTheme :
    VS2015DarkTheme
  {
    internal GitHubDarkDockTheme()
    {
      GitHubDockPalette.Apply(
        ColorPalette,
        GitHubThemeColors.Dark);
    }
  }

  internal static class GitHubDockPalette
  {
    internal static void Apply(
      DockPanelColorPalette p,
      GitHubPalette c)
    {
      // Window chrome.
      p.MainWindowActive.Background =
        c.CanvasDefault;
      p.MainWindowStatusBarDefault.Background =
        c.CanvasSubtle;
      p.MainWindowStatusBarDefault.Highlight =
        c.Accent;
      p.MainWindowStatusBarDefault.HighlightText =
        Color.White;
      p.MainWindowStatusBarDefault.Text =
        c.TextPrimary;
      p.MainWindowStatusBarDefault.ResizeGrip =
        c.TextSecondary;
      p.MainWindowStatusBarDefault.ResizeGripAccent =
        c.BorderDefault;

      // Tool-window captions use GitHub's blue emphasis only for the focused
      // pane; inactive chrome remains a neutral subtle canvas.
      p.ToolWindowCaptionActive.Background =
        c.AccentEmphasis;
      p.ToolWindowCaptionActive.Text =
        Color.White;
      p.ToolWindowCaptionActive.Button =
        Color.White;
      p.ToolWindowCaptionActive.Grip =
        Color.White;

      p.ToolWindowCaptionInactive.Background =
        c.CanvasSubtle;
      p.ToolWindowCaptionInactive.Text =
        c.TextPrimary;
      p.ToolWindowCaptionInactive.Button =
        c.TextSecondary;
      p.ToolWindowCaptionInactive.Grip =
        c.BorderDefault;

      SetHoveredButton(
        p.ToolWindowCaptionButtonActiveHovered,
        c.Accent,
        c.Accent,
        Color.White);
      SetHoveredButton(
        p.ToolWindowCaptionButtonPressed,
        c.AccentMuted,
        c.Accent,
        c.TextPrimary);
      SetHoveredButton(
        p.ToolWindowCaptionButtonInactiveHovered,
        c.AccentMuted,
        c.Accent,
        c.TextPrimary);

      // Tool-window tabs.
      p.ToolWindowTabSelectedActive.Background =
        c.AccentMuted;
      p.ToolWindowTabSelectedActive.Text =
        c.Accent;
      p.ToolWindowTabSelectedInactive.Background =
        c.CanvasSubtle;
      p.ToolWindowTabSelectedInactive.Text =
        c.TextPrimary;
      p.ToolWindowTabUnselected.Background =
        c.CanvasDefault;
      p.ToolWindowTabUnselected.Text =
        c.TextSecondary;
      p.ToolWindowTabUnselectedHovered.Background =
        c.CanvasSubtle;
      p.ToolWindowTabUnselectedHovered.Text =
        c.Accent;
      p.ToolWindowBorder =
        c.BorderDefault;
      p.ToolWindowSeparator =
        c.BorderMuted;

      // Document tabs.
      p.TabSelectedActive.Background =
        c.CanvasDefault;
      p.TabSelectedActive.Text =
        c.Accent;
      p.TabSelectedActive.Button =
        c.TextPrimary;
      p.TabSelectedInactive.Background =
        c.CanvasSubtle;
      p.TabSelectedInactive.Text =
        c.TextPrimary;
      p.TabSelectedInactive.Button =
        c.TextSecondary;
      p.TabUnselected.Background =
        c.CanvasSubtle;
      p.TabUnselected.Text =
        c.TextSecondary;
      p.TabUnselectedHovered.Background =
        c.AccentMuted;
      p.TabUnselectedHovered.Text =
        c.Accent;
      p.TabUnselectedHovered.Button =
        c.Accent;

      SetHoveredButton(
        p.TabButtonSelectedActiveHovered,
        c.AccentMuted,
        c.Accent,
        c.Accent);
      SetHoveredButton(
        p.TabButtonSelectedActivePressed,
        c.AccentEmphasis,
        c.AccentEmphasis,
        Color.White);
      SetHoveredButton(
        p.TabButtonSelectedInactiveHovered,
        c.AccentMuted,
        c.Accent,
        c.Accent);
      SetHoveredButton(
        p.TabButtonSelectedInactivePressed,
        c.AccentEmphasis,
        c.AccentEmphasis,
        Color.White);
      SetHoveredButton(
        p.TabButtonUnselectedTabHoveredButtonHovered,
        c.AccentMuted,
        c.Accent,
        c.Accent);
      SetHoveredButton(
        p.TabButtonUnselectedTabHoveredButtonPressed,
        c.AccentEmphasis,
        c.AccentEmphasis,
        Color.White);

      // Auto-hide strips and dock indicators.
      p.AutoHideStripDefault.Background =
        c.CanvasSubtle;
      p.AutoHideStripDefault.Border =
        c.BorderDefault;
      p.AutoHideStripDefault.Text =
        c.TextSecondary;
      p.AutoHideStripHovered.Background =
        c.AccentMuted;
      p.AutoHideStripHovered.Border =
        c.Accent;
      p.AutoHideStripHovered.Text =
        c.Accent;

      p.DockTarget.Background =
        c.CanvasSubtle;
      p.DockTarget.Border =
        c.Accent;
      p.DockTarget.ButtonBackground =
        c.CanvasDefault;
      p.DockTarget.ButtonBorder =
        c.BorderDefault;
      p.DockTarget.GlyphBackground =
        c.AccentMuted;
      p.DockTarget.GlyphArrow =
        c.Accent;
      p.DockTarget.GlyphBorder =
        c.Accent;

      // DockPanelSuite menu / toolbar chrome.
      p.CommandBarMenuDefault.Background =
        c.CanvasDefault;
      p.CommandBarMenuDefault.Text =
        c.TextPrimary;

      p.CommandBarMenuPopupDefault.BackgroundTop =
        c.CanvasDefault;
      p.CommandBarMenuPopupDefault.BackgroundBottom =
        c.CanvasDefault;
      p.CommandBarMenuPopupDefault.Border =
        c.BorderDefault;
      p.CommandBarMenuPopupDefault.Separator =
        c.BorderMuted;
      p.CommandBarMenuPopupDefault.IconBackground =
        c.CanvasSubtle;
      p.CommandBarMenuPopupDefault.Arrow =
        c.TextSecondary;
      p.CommandBarMenuPopupDefault.Checkmark =
        c.Accent;
      p.CommandBarMenuPopupDefault.CheckmarkBackground =
        c.AccentMuted;

      p.CommandBarMenuPopupHovered.ItemBackground =
        c.AccentMuted;
      p.CommandBarMenuPopupHovered.Text =
        c.Accent;
      p.CommandBarMenuPopupHovered.Arrow =
        c.Accent;
      p.CommandBarMenuPopupHovered.Checkmark =
        c.Accent;
      p.CommandBarMenuPopupHovered.CheckmarkBackground =
        c.AccentMuted;

      p.CommandBarMenuTopLevelHeaderHovered.Background =
        c.AccentMuted;
      p.CommandBarMenuTopLevelHeaderHovered.Border =
        c.Accent;
      p.CommandBarMenuTopLevelHeaderHovered.Text =
        c.Accent;

      p.CommandBarToolbarDefault.Background =
        c.CanvasSubtle;
      p.CommandBarToolbarDefault.Border =
        c.BorderDefault;
      p.CommandBarToolbarDefault.Grip =
        c.TextSecondary;
      p.CommandBarToolbarDefault.Separator =
        c.BorderDefault;
      p.CommandBarToolbarDefault.SeparatorAccent =
        c.CanvasDefault;
      p.CommandBarToolbarDefault.Tray =
        c.CanvasSubtle;
      p.CommandBarToolbarDefault.OverflowButtonBackground =
        c.CanvasSubtle;
      p.CommandBarToolbarDefault.OverflowButtonGlyph =
        c.TextSecondary;

      p.CommandBarToolbarButtonChecked.Background =
        c.AccentMuted;
      p.CommandBarToolbarButtonChecked.Border =
        c.Accent;
      p.CommandBarToolbarButtonChecked.Text =
        c.Accent;
      p.CommandBarToolbarButtonCheckedHovered.Border =
        c.Accent;
      p.CommandBarToolbarButtonCheckedHovered.Text =
        c.Accent;

      p.CommandBarToolbarButtonPressed.Background =
        c.AccentEmphasis;
      p.CommandBarToolbarButtonPressed.Text =
        Color.White;
      p.CommandBarToolbarButtonPressed.Arrow =
        Color.White;

      p.CommandBarToolbarOverflowHovered.Background =
        c.AccentMuted;
      p.CommandBarToolbarOverflowHovered.Glyph =
        c.Accent;
      p.CommandBarToolbarOverflowPressed.Background =
        c.AccentEmphasis;
      p.CommandBarToolbarOverflowPressed.Glyph =
        Color.White;

      p.OverflowButtonDefault.Glyph =
        c.TextSecondary;
      SetHoveredButton(
        p.OverflowButtonHovered,
        c.AccentMuted,
        c.Accent,
        c.Accent);
      SetHoveredButton(
        p.OverflowButtonPressed,
        c.AccentEmphasis,
        c.AccentEmphasis,
        Color.White);
    }

    private static void SetHoveredButton(
      HoveredButtonPalette palette,
      Color background,
      Color border,
      Color glyph)
    {
      palette.Background = background;
      palette.Border = border;
      palette.Glyph = glyph;
    }
  }
}
