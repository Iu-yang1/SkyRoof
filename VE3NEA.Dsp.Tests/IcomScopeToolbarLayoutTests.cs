using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SkyRoof;
using Xunit;

namespace VE3NEA.Dsp.Tests;

public sealed class IcomScopeToolbarLayoutTests
{
  [Theory]
  [InlineData(900, 860, 104)]
  [InlineData(860, 900, 64)]
  [InlineData(840, 980, 60)]
  public void GeometryCaptionShrinksOnlyAsMuchAsNeeded(
    int available, int required, int expectedWidth)
  {
    Assert.Equal(expectedWidth,
      IcomScopeToolbarLayout.GeometryWidth(available, required));
  }

  [Fact]
  public void CaptionLabelsAreActuallyMeasuredInsteadOfKeepingDefault100Px()
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try
      {
        using var font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10.5f);
        using var label = new Label { Text = "REF:", AutoSize = false, Font = font };
        using var selector = new ComboBox {
          DropDownStyle = ComboBoxStyle.DropDownList, Font = font };
        selector.Items.AddRange(new object[] { "AUTO", "MAIN", "SUB" });
        Assert.InRange(IcomScopeToolbarLayout.PreferredWidth(label), 25, 70);
        Assert.InRange(IcomScopeToolbarLayout.PreferredWidth(selector), 55, 110);
        Assert.True(IcomScopeToolbarLayout.PreferredWidth(label) < 100);
      }
      catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(8)));
    if (failure != null)
      throw new Xunit.Sdk.XunitException(failure.ToString());
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
    var radioExtra = typeof(IcomLanSpectrumPanel).GetField(
      "RadioSecondaryToolbar", flags);
    var display = typeof(IcomLanSpectrumPanel).GetField("DisplayToolbar", flags);
    var displayExtra = typeof(IcomLanSpectrumPanel).GetField(
      "DisplaySecondaryToolbar", flags);
    Assert.NotNull(radio);
    Assert.NotNull(display);
    Assert.Null(radioExtra);
    Assert.Null(displayExtra);
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
