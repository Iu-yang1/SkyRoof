using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Linq;
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
        // A surrogate WinForms toolbar with actual native combo boxes,
        // buttons, labels and grouped FlowLayoutPanels; unlike a two-rectangle
        // image, its bounds checks detect caption/selector separation.
        using var font = new Font("Segoe UI", 10f * dpiPercent / 100f);
        int scale = dpiPercent;
        int rowHeight = Math.Max(42, (int)Math.Ceiling(45d * scale / 100));
        using var host = new TableLayoutPanel
        {
          Size = new Size(dockWidth, rowHeight * 2 + 4),
          ColumnCount = 2, RowCount = 2,
          Margin = Padding.Empty, Padding = Padding.Empty
        };
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        host.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        using var radioRow = new FlowLayoutPanel
        {
          Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
          WrapContents = false, Margin = Padding.Empty
        };
        using var displayRow = new FlowLayoutPanel
        {
          Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
          WrapContents = false, Margin = Padding.Empty
        };
        using var actionRow = new FlowLayoutPanel
        {
          Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
          WrapContents = false, Margin = Padding.Empty
        };
        host.Controls.Add(radioRow, 0, 0);
        host.SetColumnSpan(radioRow, 2);
        host.Controls.Add(displayRow, 0, 1);
        host.Controls.Add(actionRow, 1, 1);

        int GroupWidth(FlowLayoutPanel group) =>
          group.Width + group.Margin.Horizontal;
        FlowLayoutPanel Group(string text, bool selector = false)
        {
          var group = new FlowLayoutPanel
          {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Margin = new Padding(0, 0, 3, 0),
            Height = rowHeight
          };
          Control inner = selector
            ? new ComboBox { Font = font, DropDownStyle = ComboBoxStyle.DropDownList }
            : new Button { Text = text, Font = font };
          if (inner is ComboBox combo)
          {
            combo.Items.AddRange(new object[] { text, "SCROLL-C" });
            combo.SelectedIndex = 0;
          }
          int textWidth = TextRenderer.MeasureText(
            selector ? "SCROLL-C" : text, font).Width;
          inner.Width = Math.Max(40,
            textWidth + (selector ? SystemInformation.VerticalScrollBarWidth + 10 : 18));
          inner.Height = Math.Min(rowHeight - 5, font.Height + 11);
          inner.Margin = new Padding(0, 2, 2, 1);
          group.Width = inner.Width + inner.Margin.Horizontal + 1;
          group.Controls.Add(inner);
          return group;
        }

        using var band = Group("AUTO", true);
        using var mode = Group("CENTER", true);
        using var span = Group("25 kHz", true);
        using var reference = Group("REF");
        using var speed = Group("FAST", true);
        using var vbw = Group("VBW");
        using var start = Group("STOP");
        using var clear = Group("CLR");
        using var hold = Group("HOLD");
        using var peak = Group("PEAK");
        using var waterfall = Group("WF");
        using var avg = Group("AVG");
        using var smooth = Group("SMOOTH");
        using var zoom = Group("ZOOM");
        using var settings = Group("SET");
        using var readback = Group("SYNC");
        using var more = Group("MORE");

        FlowLayoutPanel[] standardRadio = { band, mode, span, reference, speed, vbw };
        FlowLayoutPanel[] standardDisplay = { start, clear, hold, peak, waterfall, avg, smooth, zoom };
        FlowLayoutPanel[] compactRadio = { band, mode, span, reference };
        FlowLayoutPanel[] compactDisplay = { start, clear, hold, peak, waterfall, avg };
        FlowLayoutPanel[] restrictedRadio = { band, mode, span };
        FlowLayoutPanel[] restrictedDisplay = { start, hold, waterfall };
        FlowLayoutPanel[] minimalRadio = { mode, span };
        FlowLayoutPanel[] minimalDisplay = { start, hold };
        int Width(FlowLayoutPanel[] groups) =>
          groups.Sum(GroupWidth) + 8;
        int actionStandard = GroupWidth(settings) + GroupWidth(readback) + 5;
        int actionMore = actionStandard + GroupWidth(more);
        int actionMinimal = GroupWidth(settings) + GroupWidth(more) + 5;
        var density = IcomScopeToolbarLayout.Pick(dockWidth,
          Width(standardRadio), Width(standardDisplay) + actionStandard,
          Width(compactRadio), Width(compactDisplay) + actionMore,
          Width(restrictedRadio), Width(restrictedDisplay) + actionMore,
          Width(minimalRadio), Width(minimalDisplay) + actionMinimal);
        FlowLayoutPanel[] visibleRadio = density switch
        {
          ScopeToolbarDensity.Standard => standardRadio,
          ScopeToolbarDensity.Compact => compactRadio,
          ScopeToolbarDensity.Restricted => restrictedRadio,
          _ => minimalRadio
        };
        FlowLayoutPanel[] visibleDisplay = density switch
        {
          ScopeToolbarDensity.Standard => standardDisplay,
          ScopeToolbarDensity.Compact => compactDisplay,
          ScopeToolbarDensity.Restricted => restrictedDisplay,
          _ => minimalDisplay
        };
        bool compact = density != ScopeToolbarDensity.Standard;
        int reserved = density == ScopeToolbarDensity.Standard ?
          actionStandard : density == ScopeToolbarDensity.Minimal ?
          actionMinimal : actionMore;

        host.ColumnStyles[1].Width = reserved;
        foreach (var group in visibleRadio) radioRow.Controls.Add(group);
        foreach (var group in visibleDisplay) displayRow.Controls.Add(group);
        actionRow.Controls.Add(settings);
        if (density != ScopeToolbarDensity.Minimal)
          actionRow.Controls.Add(readback);
        if (compact) actionRow.Controls.Add(more);
        host.PerformLayout();
        radioRow.PerformLayout();
        displayRow.PerformLayout();
        actionRow.PerformLayout();

        foreach (var (row, controls) in new[]
          { (radioRow, visibleRadio), (displayRow, visibleDisplay) })
        {
          foreach (var group in controls)
          {
            Assert.True(IcomScopeToolbarLayout.IsWithin(
              group.Left, group.Width + group.Margin.Right,
              row.ClientSize.Width),
              $"{dockWidth}px at {dpiPercent}% {density}: {group.Name} " +
              $"right={group.Right}, row={row.ClientSize.Width}");
            Assert.Single(group.Controls);
            var child = group.Controls[0];
            Assert.True(IcomScopeToolbarLayout.IsWithin(
              child.Left, child.Width + child.Margin.Right,
              group.ClientSize.Width));
          }
        }
        foreach (Control item in actionRow.Controls)
          Assert.True(IcomScopeToolbarLayout.IsWithin(item.Left, item.Width,
            actionRow.ClientSize.Width));

        using var image = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
        string? dir = Environment.GetEnvironmentVariable(
          "SKYROOF_SCOPE_LAYOUT_ARTIFACT_DIR");
        if (!string.IsNullOrWhiteSpace(dir))
        {
          Directory.CreateDirectory(dir);
          image.Save(Path.Combine(dir,
            $"scope-two-row-{dockWidth}px-dpi{dpiPercent}.png"));
        }
      }
      catch (Exception ex) { failure = ex; }
    });
    t.SetApartmentState(ApartmentState.STA);
    t.Start();
    Assert.True(t.Join(TimeSpan.FromSeconds(10)),
      "Windows WinForms surrogate must render");
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
