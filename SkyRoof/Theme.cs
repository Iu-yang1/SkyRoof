using Newtonsoft.Json.Linq;
using Serilog;

namespace SkyRoof
{
  public enum ThemeMode { System, Light, Dark }

  // The application theme. Application.SetColorMode() lets the framework and the OS render the
  // common controls, the menus, the scroll bars and the title bar, and it flips every SystemColors
  // member, so this class only has to answer for the colors that carry a meaning of their own.
  // See design-docs/theme_switching_plan.md, sections 2, 4.1 and 4.3.
  public static class Theme
  {
    public static ThemeMode Mode { get; private set; }
    public static bool IsDark { get; private set; }

    // The theme is a startup setting: SetColorMode() and the DockPanelSuite theme must both be
    // selected before any window exists, and the DockPanel.Theme setter throws once dock content
    // is open. Called from Program.Main, before the settings are loaded into Settings.Ui, so the
    // mode is read straight from the settings file.
    public static void Initialize()
    {
      Mode = ReadModeFromSettingsFile();

      try
      {
        Application.SetColorMode(ToColorMode(Mode));
      }
      catch (Exception ex)
      {
        // Wine 9 may not implement the underlying dark mode support, stay in light mode
        Log.Warning(ex, "SetColorMode failed");
      }

      // in System mode this follows the OS setting
      IsDark = Application.IsDarkModeEnabled;

      VhfTintBrush = new SolidBrush(VhfTint);
      UhfTintBrush = new SolidBrush(UhfTint);
      NowBrush = new SolidBrush(Now);
    }

    private static SystemColorMode ToColorMode(ThemeMode mode)
    {
      return mode switch
      {
        ThemeMode.Light => SystemColorMode.Classic,
        ThemeMode.Dark => SystemColorMode.Dark,
        _ => SystemColorMode.System
      };
    }

    private static ThemeMode ReadModeFromSettingsFile()
    {
      try
      {
        string fileName = Settings.GetFileName();
        if (!File.Exists(fileName)) return ThemeMode.Light;

        var token = JObject.Parse(File.ReadAllText(fileName)).SelectToken("Ui.Theme");
        if (token == null) return ThemeMode.Light;

        // Newtonsoft writes the enum as a number, Enum.TryParse accepts both forms
        return Enum.TryParse(token.ToString(), out ThemeMode mode) ? mode : ThemeMode.Light;
      }
      catch (Exception ex)
      {
        Log.Warning(ex, "Failed to read the theme setting");
        return ThemeMode.Light;
      }
    }




    // ----------------------------------------------------------------------------------------------
    //                                        themed colors
    // ----------------------------------------------------------------------------------------------
    // Each entry names a role and gives its value in both themes. Colors that are the same in both
    // themes do not belong here: the frequency and az/el readouts, the QSO entry field rings, the
    // status LEDs, the FT4 message colors and the waterfall palette are all deliberately fixed.
    // The color sweep adds the remaining entries.
    private static Color Pick(Color light, Color dark) { return IsDark ? dark : light; }

    // SkyRoof light-theme identity. These three exact colors are the user-facing
    // palette; the washes/darker inks below are derived companions used where
    // pure brand colors would not provide enough text or border contrast.
    public static readonly Color BrandBlue =
      Color.FromArgb(0x5B, 0xCE, 0xFA);
    public static readonly Color BrandPink =
      Color.FromArgb(0xF5, 0xA9, 0xB8);
    public static readonly Color BrandWhite =
      Color.FromArgb(0xFF, 0xFF, 0xFF);

    internal static readonly Color LightInk =
      Color.FromArgb(0x24, 0x31, 0x3A);
    internal static readonly Color BlueDark =
      Color.FromArgb(0x17, 0x6A, 0x8C);
    internal static readonly Color PinkDark =
      Color.FromArgb(0xA6, 0x47, 0x5B);

    internal static readonly Color BlueWash =
      Color.FromArgb(0xD6, 0xF3, 0xFE);
    internal static readonly Color BlueSoft =
      Color.FromArgb(0xC6, 0xEE, 0xFD);
    internal static readonly Color PinkWash =
      Color.FromArgb(0xFD, 0xEA, 0xED);
    internal static readonly Color PinkSoft =
      Color.FromArgb(0xFC, 0xE1, 0xE6);

    // tooltips: the framework paints them light in both modes, ToolTipEx repaints them
    public static Color TipBack => Pick(SystemColors.Info, SystemColors.ControlLight);
    public static Color TipText => Pick(SystemColors.InfoText, SystemColors.ControlText);

    // Section headers in list views, and the rule that trails the header text. The theme paints
    // them a dark blue that the dark surface swallows and offers no color of its own, so
    // ListViewEx paints the header itself; the light values are the ones it used to paint.
    public static Color ListGroupText => Pick(BlueDark, Color.FromArgb(138, 180, 248));
    public static Color ListGroupRule => Pick(BlueSoft, Color.FromArgb(45, 45, 45));

    // hyperlinks. Blue is barely readable on the dark surface, aqua replaces it there
    public static Color Link => Pick(BlueDark, Color.Aqua);

    // Sky view. The plot surface itself is SystemColors.Window and needs no entry here. In the
    // dark theme the disks on it are pulled between two constraints: light enough for the
    // satellite icon (a #0041AC body) to read against them, dark enough for the satellite names,
    // which are WindowText. The values below are the lightest that keep the names at 3:1;
    // going lighter means painting the names dark instead.
    public static Color SkyRealTimeDisk => Pick(BlueWash, Color.FromArgb(120, 138, 155));
    public static Color SkyOrbitDisk => Pick(PinkWash, Color.FromArgb(125, 125, 125));

    // Band tints, marking the downlink band of a satellite or a transmitter. The light theme
    // uses pink for VHF and blue for UHF; both are deliberately pale washes so WindowText remains
    // readable. The dark-theme values stay unchanged.
    public static Color VhfTint => Pick(PinkWash, Color.FromArgb(86, 74, 30));
    public static Color UhfTint => Pick(BlueWash, Color.FromArgb(30, 80, 92));

    // built in Initialize, once IsDark is known: a static field initializer would run at type
    // init, which happens on the way into Initialize itself
    public static Brush VhfTintBrush { get; private set; } = Brushes.LightGoldenrodYellow;
    public static Brush UhfTintBrush { get; private set; } = Brushes.LightCyan;

    // Text on a list row, tinted or not.
    public static Color RowText(bool inactive)
    {
      return inactive ? SystemColors.GrayText : SystemColors.WindowText;
    }

    public static Brush RowTextBrush(bool inactive)
    {
      return inactive ? SystemBrushes.GrayText : SystemBrushes.WindowText;
    }

    // QSO entry: the card behind each field, and the field's own ring while untouched - the two
    // share a color on purpose, so an untouched ring disappears into its card
    public static Color QsoCard => Pick(BlueWash, Color.FromArgb(34, 48, 60));

    // the ring around a field the operator has edited. QsoEntryPanel stores field state in this
    // color and compares against it (plan 4.2), so it must round-trip through BackColor: keep it
    // a single Theme entry, and never restate either value as a FromArgb literal elsewhere
    public static Color QsoFieldEdited => Pick(PinkDark, Color.DodgerBlue);

    // The unfilled part of the FT4 bars. ControlLightLight is white in light mode but #1F1F1F in
    // dark, where the bar then disappears into the panel, so the dark end is silver instead.
    public static Color BarRemainder => Pick(Color.White, Color.Silver);

    // "now" marker on a pass row, and the pass path in the mini sky views, which the marker sits
    // on: green is too dark to read on the dark row surface
    public static Color Now => Pick(Color.Green, Color.Lime);
    public static Brush NowBrush { get; private set; } = Brushes.Green;

    // timeline chart: the light theme runs from the exact brand blue overhead to white at the
    // horizon. The dark theme keeps its existing high-contrast gradient.
    public static Color TimelineTop => Pick(BrandBlue, Color.Black);
    public static Color TimelineBottom => Pick(BrandWhite, Color.RoyalBlue);

    // Earth view: the space around the globe. The light value is the 0.7 gray the panel always
    // cleared to - darker than the panel around it, and the dark value is lighter than its panel,
    // so the surround stays distinct from the chrome in both themes
    public static Color EarthSpace => Pick(PinkSoft, Color.FromArgb(64, 64, 64));

    // the DXCC world map is a light bitmap and stays content, not chrome: the fragment shader
    // dims it as a whole in the dark theme rather than recoloring it
    public static float EarthMapBrightness => IsDark ? 0.7f : 1;

    // frequency scale: the accent marks the pass that is happening now - its label text, the line
    // under the label, and the frame around the active span
    public static Color ScaleAccent => Pick(BlueDark, Color.SkyBlue);

    // the transponder span is a wash over the scale. A 20/255 tint that reads on #F0F0F0
    // disappears on #202020, so the dark alpha is doubled
    public static Color ScaleActiveSpan => Pick(Color.FromArgb(55, BrandPink), Color.FromArgb(40, Color.Aqua));
    public static Color ScaleIdleSpan => Pick(Color.FromArgb(20, Color.Gray), Color.FromArgb(40, Color.Gray));

    // receiver passband: the light theme uses the blue identity color; the dark theme keeps the
    // existing green/lime treatment for contrast on the dark surface.
    public static Color PassbandFill => Pick(Color.FromArgb(95, BrandBlue), Color.FromArgb(200, Color.Green));
    public static Color PassbandFrame => Pick(BlueDark, Color.Lime);

    // Signal Details provenance: the color of a value a decoded frame has confirmed - the field dots, the
    // gear glyph and the dialog's status line. The text is what sets the requirement: LimeGreen carries a
    // dot on either ground but washes out as text on #F0F0F0, the same 1.5:1 the passband above trades
    // places over, so the light theme gets a dark green. The edited color stays Color.Orange in both
    // themes and is not an entry here, by the rule at the top of this section.
    public static Color ParamsConfirmed => Pick(Color.Green, Color.LimeGreen);
  }
}
