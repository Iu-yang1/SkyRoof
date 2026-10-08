namespace SkyRoof
{
  // ProfessionalRenderer palette for the application's own MenuStrip and
  // StatusStrip. DockPanelSuite has its separate palette in
  // PinkBlueLightDockTheme.
  internal sealed class PinkBlueToolStripColorTable :
    ProfessionalColorTable
  {
    public override Color MenuStripGradientBegin =>
      Theme.BrandWhite;
    public override Color MenuStripGradientEnd =>
      Theme.BrandWhite;

    public override Color MenuItemSelected =>
      Theme.BlueWash;
    public override Color MenuItemSelectedGradientBegin =>
      Theme.BlueWash;
    public override Color MenuItemSelectedGradientEnd =>
      Theme.BlueWash;
    public override Color MenuItemBorder =>
      Theme.BrandBlue;

    public override Color MenuItemPressedGradientBegin =>
      Theme.PinkWash;
    public override Color MenuItemPressedGradientMiddle =>
      Theme.PinkWash;
    public override Color MenuItemPressedGradientEnd =>
      Theme.PinkWash;

    public override Color ToolStripDropDownBackground =>
      Theme.BrandWhite;
    public override Color MenuBorder =>
      Theme.BlueSoft;

    public override Color ImageMarginGradientBegin =>
      Theme.BlueWash;
    public override Color ImageMarginGradientMiddle =>
      Theme.BlueWash;
    public override Color ImageMarginGradientEnd =>
      Theme.PinkWash;

    public override Color SeparatorDark =>
      Theme.PinkSoft;
    public override Color SeparatorLight =>
      Theme.BrandWhite;

    public override Color CheckBackground =>
      Theme.PinkWash;
    public override Color CheckSelectedBackground =>
      Theme.BrandPink;
    public override Color CheckPressedBackground =>
      Theme.BrandBlue;

    public override Color StatusStripGradientBegin =>
      Theme.BrandPink;
    public override Color StatusStripGradientEnd =>
      Theme.BrandPink;

    public override Color ToolStripBorder =>
      Theme.BlueSoft;
  }
}
