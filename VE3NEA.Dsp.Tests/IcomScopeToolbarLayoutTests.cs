using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class IcomScopeToolbarLayoutTests
{
  [Theory]
  [InlineData(880, 100, "Standard")]
  [InlineData(880, 125, "Compact")]
  [InlineData(880, 150, "Restricted")]
  [InlineData(880, 200, "Minimal")]
  [InlineData(560, 100, "Restricted")]
  [InlineData(560, 125, "Minimal")]
  [InlineData(560, 150, "Minimal")]
  [InlineData(560, 200, "Minimal")]
  public void WidthBudgetSelectsTwoRowLayoutsWithoutShrinkingFonts(
    int available, int percent, string expectedName)
  {
    int Scale(int size) => (int)Math.Ceiling(size * percent / 100.0);
    ScopeToolbarDensity mode = IcomScopeToolbarLayout.Pick(
      available,
      Scale(810), Scale(855), Scale(580), Scale(625),
      Scale(430), Scale(460), Scale(245), Scale(265));
    Assert.Equal(Enum.Parse<ScopeToolbarDensity>(expectedName), mode);

    var selected = mode switch
    {
      ScopeToolbarDensity.Standard => (Scale(810), Scale(855)),
      ScopeToolbarDensity.Compact => (Scale(580), Scale(625)),
      ScopeToolbarDensity.Restricted => (Scale(430), Scale(460)),
      _ => (Scale(245), Scale(265))
    };
    Assert.True(IcomScopeToolbarLayout.Fits(available,
      selected.Item1, selected.Item2));
  }

  [Fact]
  public void NoWidthNeverClaimsStandardModeFits()
  {
    Assert.Equal(ScopeToolbarDensity.Minimal,
      IcomScopeToolbarLayout.Pick(0, 5, 5, 4, 4, 3, 3, 2, 2));
  }

  [Theory]
  [InlineData(0, 50, 100, true)]
  [InlineData(50, 50, 100, true)]
  [InlineData(50, 51, 100, false)]
  [InlineData(-1, 10, 100, false)]
  public void BoundsCheckerDetectsHorizontalClipping(
    int x, int width, int client, bool expected)
  {
    Assert.Equal(expected, IcomScopeToolbarLayout.IsWithin(x, width, client));
  }

  // P3: Windows CI creates a representative two-row rendering matrix at
  // 100/125/150/200% font sizes and four dock widths. These are layout
  // *fixtures*, not screenshots of a connected IC-9700 panel.
  [Theory]
  [InlineData(560, 100)]
  [InlineData(560, 125)]
  [InlineData(560, 150)]
  [InlineData(560, 200)]
  [InlineData(720, 100)]
  [InlineData(720, 125)]
  [InlineData(720, 150)]
  [InlineData(720, 200)]
  [InlineData(880, 100)]
  [InlineData(880, 125)]
  [InlineData(880, 150)]
  [InlineData(880, 200)]
  [InlineData(1200, 100)]
  [InlineData(1200, 125)]
  [InlineData(1200, 150)]
  [InlineData(1200, 200)]
  public void TwoRowFixtureRendersWithoutOverlap(
    int dockWidth, int dpiPercent)
  {
    Exception? failure = null;
    var t = new Thread(() =>
    {
      try
      {
        using var font = new Font("Segoe UI", 10f * dpiPercent / 100f);
        using var pane = new Panel
        {
          Size = new Size(dockWidth, Math.Max(96, 90 * dpiPercent / 100)),
          BackColor = Color.FromArgb(238, 246, 251)
        };
        int rowHeight = pane.Height / 2;
        int[] fullWidths = { 810, 855 };
        int[] compactWidths = { 580, 625 };
        int[] restrictedWidths = { 430, 460 };
        int[] minimalWidths = { 245, 265 };
        int Scale(int size) => (int)Math.Ceiling(size * dpiPercent / 100.0);
        var mode = IcomScopeToolbarLayout.Pick(dockWidth,
          Scale(fullWidths[0]), Scale(fullWidths[1]),
          Scale(compactWidths[0]), Scale(compactWidths[1]),
          Scale(restrictedWidths[0]), Scale(restrictedWidths[1]),
          Scale(minimalWidths[0]), Scale(minimalWidths[1]));
        var selected = mode switch
        {
          ScopeToolbarDensity.Standard => fullWidths,
          ScopeToolbarDensity.Compact => compactWidths,
          ScopeToolbarDensity.Restricted => restrictedWidths,
          _ => minimalWidths
        };
        for (int row = 0; row < 2; row++)
        {
          int total = Scale(selected[row]);
          Assert.True(IcomScopeToolbarLayout.IsWithin(0, total, dockWidth));
          var marker = new Label
          {
            Bounds = new Rectangle(0, row * rowHeight, total, rowHeight - 2),
            Font = font, BorderStyle = BorderStyle.FixedSingle,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = row == 0 ? $"RADIO / {mode}" : $"DISPLAY / {mode}"
          };
          pane.Controls.Add(marker);
        }
        using var bitmap = new Bitmap(pane.Width, pane.Height);
        pane.DrawToBitmap(bitmap, new Rectangle(Point.Empty, pane.Size));
        string? dir = Environment.GetEnvironmentVariable(
          "SKYROOF_SCOPE_LAYOUT_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
          Directory.CreateDirectory(dir);
          bitmap.Save(Path.Combine(dir,
            $"scope-two-row-{dockWidth}px-dpi{dpiPercent}.png"));
        }
      }
      catch (Exception ex) { failure = ex; }
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    Assert.True(t.Join(TimeSpan.FromSeconds(10)));
    if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
  }

  [Theory]
  [InlineData(ThemeMode.Light, false)]
  [InlineData(ThemeMode.Dark, true)]
  [InlineData(ThemeMode.GitHubLight, false)]
  [InlineData(ThemeMode.GitHubDark, true)]
  public void PaletteHasLegibleTextAndActiveToggle(ThemeMode mode, bool dark)
  {
    var colors = ScopeUiColors.For(mode, dark);
    Assert.NotEqual(colors.Background, colors.Text);
    Assert.NotEqual(colors.Surface, colors.Accent);
    Assert.NotEqual(colors.Text, colors.Muted);
    Assert.NotEqual(Color.Empty, colors.Border);
  }

  [Fact]
  public void ThemeSystemFollowsActiveDarkSetting()
  {
    var light = ScopeUiColors.For(ThemeMode.System, false);
    var dark = ScopeUiColors.For(ThemeMode.System, true);
    Assert.NotEqual(light.Background, dark.Background);
  }

  [Fact]
  public void OperatorStatusNeverClaimsUnconfirmedCatWritesAreSynced()
  {
    Assert.Contains("等待电台确认", IcomScopeStatusSummary.Format(
      "Native LAN", 28.1, "SkyCAT", false, true, true, 2, 0));
    Assert.Contains("操作超时", IcomScopeStatusSummary.Format(
      "Native LAN", 28.1, "SkyCAT", false, true, true, 0, 1));
    Assert.Contains("已同步", IcomScopeStatusSummary.Format(
      "Native LAN", 28.1, "SkyCAT", false, true, true, 0, 0));
    Assert.Contains("只读", IcomScopeStatusSummary.Format(
      "RS-BA1", 28.1, "Read only", true, false, false, 0, 0));
    Assert.Contains("控制后端未连接", IcomScopeStatusSummary.Format(
      "SkyCAT", 0, "SkyCAT", false, false, false, 0, 0));
    Assert.Contains("待同步", IcomScopeStatusSummary.Format(
      "SkyCAT", 0, "SkyCAT", false, true, false, 0, 0));
  }

  [Fact]
  public void BothToolbarGroupsHaveDedicatedWinFormsControls()
  {
    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var radio = typeof(IcomLanSpectrumPanel).GetField("RadioToolbar", flags);
    var display = typeof(IcomLanSpectrumPanel).GetField("DisplayToolbar", flags);
    Assert.NotNull(radio);
    Assert.NotNull(display);
    Assert.Equal(typeof(FlowLayoutPanel), radio!.FieldType);
    Assert.Equal(typeof(FlowLayoutPanel), display!.FieldType);
  }

  [Fact]
  public void StyledControlsDrawInStaWithoutChangingRadioState()
  {
    Exception? failure = null;
    var t = new Thread(() =>
    {
      try
      {
        var p = ScopeUiColors.For(ThemeMode.GitHubDark);
        using var panel = new Panel { Size = new Size(600, 72),
          BackColor = p.Surface };
        using var label = new Label { Text = "电台", ForeColor = p.Muted,
          Size = new Size(50, 28) };
        using var selector = new ComboBox { Width = 100,
          DropDownStyle = ComboBoxStyle.DropDownList };
        selector.Items.AddRange(new object[] { "AUTO", "MAIN", "SUB" });
        selector.SelectedIndex = 0;
        panel.Controls.Add(label);
        panel.Controls.Add(selector);
        using var image = new Bitmap(600, 72);
        panel.DrawToBitmap(image, new Rectangle(0, 0, 600, 72));
        Assert.Equal("AUTO", selector.SelectedItem);
      }
      catch (Exception ex) { failure = ex; }
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    Assert.True(t.Join(TimeSpan.FromSeconds(8)), "WinForms render must complete");
    if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
  }
}
