namespace SkyRoof
{
  internal sealed class GitHubToolStripColorTable :
    ProfessionalColorTable
  {
    private readonly GitHubPalette Palette;

    internal GitHubToolStripColorTable(
      GitHubPalette palette)
    {
      Palette = palette;
      UseSystemColors = false;
    }

    public override Color MenuStripGradientBegin =>
      Palette.CanvasInset;
    public override Color MenuStripGradientEnd =>
      Palette.CanvasInset;

    public override Color MenuItemSelected =>
      Palette.AccentMuted;
    public override Color MenuItemSelectedGradientBegin =>
      Palette.AccentMuted;
    public override Color MenuItemSelectedGradientEnd =>
      Palette.AccentMuted;
    public override Color MenuItemBorder =>
      Palette.Accent;

    public override Color MenuItemPressedGradientBegin =>
      Palette.CanvasDefault;
    public override Color MenuItemPressedGradientMiddle =>
      Palette.CanvasDefault;
    public override Color MenuItemPressedGradientEnd =>
      Palette.CanvasDefault;

    public override Color ToolStripDropDownBackground =>
      Palette.CanvasDefault;
    public override Color MenuBorder =>
      Palette.BorderDefault;

    public override Color ImageMarginGradientBegin =>
      Palette.CanvasSubtle;
    public override Color ImageMarginGradientMiddle =>
      Palette.CanvasSubtle;
    public override Color ImageMarginGradientEnd =>
      Palette.CanvasSubtle;

    public override Color SeparatorDark =>
      Palette.BorderDefault;
    public override Color SeparatorLight =>
      Palette.BorderMuted;

    public override Color CheckBackground =>
      Palette.AccentMuted;
    public override Color CheckSelectedBackground =>
      Palette.AccentMuted;
    public override Color CheckPressedBackground =>
      Palette.AccentEmphasis;

    public override Color StatusStripGradientBegin =>
      Palette.CanvasSubtle;
    public override Color StatusStripGradientEnd =>
      Palette.CanvasSubtle;

    public override Color ToolStripBorder =>
      Palette.BorderDefault;

    public override Color ButtonSelectedBorder =>
      Palette.Accent;
    public override Color ButtonSelectedHighlight =>
      Palette.AccentMuted;
    public override Color ButtonSelectedHighlightBorder =>
      Palette.Accent;

    public override Color ButtonPressedBorder =>
      Palette.AccentEmphasis;
    public override Color ButtonPressedHighlight =>
      Palette.AccentMuted;
    public override Color ButtonPressedHighlightBorder =>
      Palette.Accent;
  }

  internal sealed class GitHubToolStripRenderer :
    ToolStripProfessionalRenderer
  {
    private readonly GitHubPalette Palette;

    internal GitHubToolStripRenderer(
      GitHubPalette palette)
      : base(
        new GitHubToolStripColorTable(
          palette))
    {
      Palette = palette;
      RoundedEdges = false;
    }

    protected override void OnRenderItemText(
      ToolStripItemTextRenderEventArgs e)
    {
      if (e.ToolStrip is MenuStrip ||
          e.ToolStrip is ToolStripDropDown)
      {
        e.TextColor =
          e.Item.Enabled
            ? Palette.TextPrimary
            : Palette.TextSecondary;
      }

      base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(
      ToolStripArrowRenderEventArgs e)
    {
      ToolStrip? owner =
        e.Item.Owner;

      if (owner is MenuStrip ||
          owner is ToolStripDropDown)
        e.ArrowColor =
          e.Item.Enabled
            ? Palette.TextSecondary
            : Palette.BorderDefault;

      base.OnRenderArrow(e);
    }
  }
}
