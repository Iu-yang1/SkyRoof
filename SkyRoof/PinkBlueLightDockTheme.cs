using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  // Light-theme DockPanel chrome using SkyRoof's pink / blue / white palette.
  // This derives from VS2015LightTheme so all existing factories, glyphs and
  // layout behavior stay unchanged; only the mutable color palette is replaced.
  internal sealed class PinkBlueLightDockTheme : VS2015LightTheme
  {
    internal PinkBlueLightDockTheme()
    {
      DockPanelColorPalette p =
        ColorPalette;

      // Main surface / status bar.
      p.MainWindowActive.Background =
        Theme.BrandWhite;
      p.MainWindowStatusBarDefault.Background =
        Theme.BrandPink;
      p.MainWindowStatusBarDefault.Highlight =
        Theme.BrandBlue;
      p.MainWindowStatusBarDefault.HighlightText =
        Theme.LightInk;
      p.MainWindowStatusBarDefault.Text =
        Theme.LightInk;
      p.MainWindowStatusBarDefault.ResizeGrip =
        Theme.LightInk;
      p.MainWindowStatusBarDefault.ResizeGripAccent =
        Theme.BrandWhite;

      // Tool-window captions: active blue, inactive soft pink.
      p.ToolWindowCaptionActive.Background =
        Theme.BrandBlue;
      p.ToolWindowCaptionActive.Text =
        Theme.LightInk;
      p.ToolWindowCaptionActive.Button =
        Theme.LightInk;
      p.ToolWindowCaptionActive.Grip =
        Theme.LightInk;

      p.ToolWindowCaptionInactive.Background =
        Theme.PinkWash;
      p.ToolWindowCaptionInactive.Text =
        Theme.LightInk;
      p.ToolWindowCaptionInactive.Button =
        Theme.LightInk;
      p.ToolWindowCaptionInactive.Grip =
        Theme.PinkDark;

      SetHoveredButton(
        p.ToolWindowCaptionButtonActiveHovered,
        Theme.BrandPink,
        Theme.PinkDark,
        Theme.LightInk);
      SetHoveredButton(
        p.ToolWindowCaptionButtonPressed,
        Theme.PinkDark,
        Theme.PinkDark,
        Theme.BrandWhite);
      SetHoveredButton(
        p.ToolWindowCaptionButtonInactiveHovered,
        Theme.BlueWash,
        Theme.BrandBlue,
        Theme.LightInk);

      // Tool-window tabs: selected pink, unselected white, blue hover.
      p.ToolWindowTabSelectedActive.Background =
        Theme.BrandPink;
      p.ToolWindowTabSelectedActive.Text =
        Theme.LightInk;
      p.ToolWindowTabSelectedInactive.Background =
        Theme.PinkWash;
      p.ToolWindowTabSelectedInactive.Text =
        Theme.LightInk;
      p.ToolWindowTabUnselected.Background =
        Theme.BrandWhite;
      p.ToolWindowTabUnselected.Text =
        Theme.LightInk;
      p.ToolWindowTabUnselectedHovered.Background =
        Theme.BlueWash;
      p.ToolWindowTabUnselectedHovered.Text =
        Theme.LightInk;
      p.ToolWindowBorder =
        Theme.BlueSoft;
      p.ToolWindowSeparator =
        Theme.PinkSoft;

      // Document tabs follow the inverse rhythm: selected blue, pink hover.
      p.TabSelectedActive.Background =
        Theme.BrandBlue;
      p.TabSelectedActive.Text =
        Theme.LightInk;
      p.TabSelectedActive.Button =
        Theme.LightInk;
      p.TabSelectedInactive.Background =
        Theme.BlueWash;
      p.TabSelectedInactive.Text =
        Theme.LightInk;
      p.TabSelectedInactive.Button =
        Theme.LightInk;
      p.TabUnselected.Background =
        Theme.BrandWhite;
      p.TabUnselected.Text =
        Theme.LightInk;
      p.TabUnselectedHovered.Background =
        Theme.PinkWash;
      p.TabUnselectedHovered.Text =
        Theme.LightInk;
      p.TabUnselectedHovered.Button =
        Theme.LightInk;

      SetHoveredButton(
        p.TabButtonSelectedActiveHovered,
        Theme.PinkWash,
        Theme.BrandPink,
        Theme.LightInk);
      SetHoveredButton(
        p.TabButtonSelectedActivePressed,
        Theme.BrandPink,
        Theme.PinkDark,
        Theme.LightInk);
      SetHoveredButton(
        p.TabButtonSelectedInactiveHovered,
        Theme.BlueWash,
        Theme.BrandBlue,
        Theme.LightInk);
      SetHoveredButton(
        p.TabButtonSelectedInactivePressed,
        Theme.BrandBlue,
        Theme.BlueDark,
        Theme.LightInk);
      SetHoveredButton(
        p.TabButtonUnselectedTabHoveredButtonHovered,
        Theme.PinkWash,
        Theme.BrandPink,
        Theme.LightInk);
      SetHoveredButton(
        p.TabButtonUnselectedTabHoveredButtonPressed,
        Theme.BrandPink,
        Theme.PinkDark,
        Theme.LightInk);

      // Auto-hide tabs.
      p.AutoHideStripDefault.Background =
        Theme.BrandWhite;
      p.AutoHideStripDefault.Border =
        Theme.BlueSoft;
      p.AutoHideStripDefault.Text =
        Theme.LightInk;
      p.AutoHideStripHovered.Background =
        Theme.PinkWash;
      p.AutoHideStripHovered.Border =
        Theme.BrandPink;
      p.AutoHideStripHovered.Text =
        Theme.LightInk;

      // Dock indicators keep the white center but use both accent colors.
      p.DockTarget.Background =
        Theme.BlueWash;
      p.DockTarget.Border =
        Theme.BrandBlue;
      p.DockTarget.ButtonBackground =
        Theme.BrandWhite;
      p.DockTarget.ButtonBorder =
        Theme.BrandPink;
      p.DockTarget.GlyphBackground =
        Theme.PinkWash;
      p.DockTarget.GlyphArrow =
        Theme.BlueDark;
      p.DockTarget.GlyphBorder =
        Theme.PinkDark;

      // Menu / toolbar chrome. The renderer created by VS2015LightTheme holds
      // this same palette instance, so these mutations apply without replacing
      // DockPanelSuite's rendering machinery.
      p.CommandBarMenuDefault.Background =
        Theme.BrandWhite;
      p.CommandBarMenuDefault.Text =
        Theme.LightInk;

      p.CommandBarMenuPopupDefault.BackgroundTop =
        Theme.BrandWhite;
      p.CommandBarMenuPopupDefault.BackgroundBottom =
        Theme.BrandWhite;
      p.CommandBarMenuPopupDefault.Border =
        Theme.BlueSoft;
      p.CommandBarMenuPopupDefault.Separator =
        Theme.PinkSoft;
      p.CommandBarMenuPopupDefault.IconBackground =
        Theme.BlueWash;
      p.CommandBarMenuPopupDefault.Arrow =
        Theme.LightInk;
      p.CommandBarMenuPopupDefault.Checkmark =
        Theme.LightInk;
      p.CommandBarMenuPopupDefault.CheckmarkBackground =
        Theme.PinkWash;

      p.CommandBarMenuPopupHovered.ItemBackground =
        Theme.PinkWash;
      p.CommandBarMenuPopupHovered.Text =
        Theme.LightInk;
      p.CommandBarMenuPopupHovered.Arrow =
        Theme.LightInk;
      p.CommandBarMenuPopupHovered.Checkmark =
        Theme.LightInk;
      p.CommandBarMenuPopupHovered.CheckmarkBackground =
        Theme.BrandBlue;

      p.CommandBarMenuTopLevelHeaderHovered.Background =
        Theme.BlueWash;
      p.CommandBarMenuTopLevelHeaderHovered.Border =
        Theme.BrandBlue;
      p.CommandBarMenuTopLevelHeaderHovered.Text =
        Theme.LightInk;

      p.CommandBarToolbarDefault.Background =
        Theme.BrandWhite;
      p.CommandBarToolbarDefault.Border =
        Theme.BlueSoft;
      p.CommandBarToolbarDefault.Grip =
        Theme.BrandPink;
      p.CommandBarToolbarDefault.Separator =
        Theme.PinkSoft;
      p.CommandBarToolbarDefault.SeparatorAccent =
        Theme.BrandWhite;
      p.CommandBarToolbarDefault.Tray =
        Theme.BrandWhite;
      p.CommandBarToolbarDefault.OverflowButtonBackground =
        Theme.BlueWash;
      p.CommandBarToolbarDefault.OverflowButtonGlyph =
        Theme.LightInk;

      p.CommandBarToolbarButtonChecked.Background =
        Theme.PinkWash;
      p.CommandBarToolbarButtonChecked.Border =
        Theme.BrandPink;
      p.CommandBarToolbarButtonChecked.Text =
        Theme.LightInk;
      p.CommandBarToolbarButtonCheckedHovered.Border =
        Theme.BrandBlue;
      p.CommandBarToolbarButtonCheckedHovered.Text =
        Theme.LightInk;

      p.CommandBarToolbarButtonPressed.Background =
        Theme.BrandBlue;
      p.CommandBarToolbarButtonPressed.Text =
        Theme.LightInk;
      p.CommandBarToolbarButtonPressed.Arrow =
        Theme.LightInk;

      p.CommandBarToolbarOverflowHovered.Background =
        Theme.BlueWash;
      p.CommandBarToolbarOverflowHovered.Glyph =
        Theme.LightInk;
      p.CommandBarToolbarOverflowPressed.Background =
        Theme.BrandPink;
      p.CommandBarToolbarOverflowPressed.Glyph =
        Theme.LightInk;

      p.OverflowButtonDefault.Glyph =
        Theme.LightInk;
      SetHoveredButton(
        p.OverflowButtonHovered,
        Theme.BlueWash,
        Theme.BrandBlue,
        Theme.LightInk);
      SetHoveredButton(
        p.OverflowButtonPressed,
        Theme.BrandPink,
        Theme.PinkDark,
        Theme.LightInk);
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
