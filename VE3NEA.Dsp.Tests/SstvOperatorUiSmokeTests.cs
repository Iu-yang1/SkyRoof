using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SkyRoof;
using SkyRoof.SSTV;
using Xunit;

namespace VE3NEA.Dsp.Tests
{
  /// <summary>
  /// Actual WinForms control and click tests, not merely textual assertions.
  /// Runs on an STA just like the user's Windows application and never
  /// connects to CAT/PTT or opens a blocking device-selection dialog.
  /// </summary>
  public sealed class SstvOperatorUiSmokeTests
  {
    private static T Field<T>(object target, string name)
    {
      var field = target.GetType().GetField(
        name, BindingFlags.NonPublic | BindingFlags.Instance);
      return (T)(field?.GetValue(target) ??
        throw new InvalidOperationException("Missing UI field " + name));
    }

    private static void Invoke(object target, string name, params object[] args)
    {
      MethodInfo method = target.GetType().GetMethod(
        name, BindingFlags.NonPublic | BindingFlags.Instance) ??
        throw new InvalidOperationException("Missing UI action " + name);
      method.Invoke(target, args);
    }

    [Fact]
    public void FourTabsAndImageEditorWorkWithoutAnyRadioOrPtt()
    {
      Exception? failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          using var ctx = new UiFixture();
          var panel = ctx.Panel;
          panel.Size = new Size(980, 720);
          panel.CreateControl();
          var tabs = panel.Controls.OfType<TabControl>().Single();
          Assert.Equal(new[] { "接收 RX", "发送 TX",
              "图库 / 历史", "通联日志" },
            tabs.TabPages.Cast<TabPage>().Select(x => x.Text).ToArray());
          Assert.Equal(43, Field<ComboBox>(panel, "modeBox").Items.Count);
          Assert.False(Field<CheckBox>(panel, "txApproval").Checked);
          Assert.False(Field<Button>(panel, "txStopButton").Enabled);
          Assert.True(Field<CheckBox>(panel, "autoVisCorrection").Checked);
          Assert.Equal(500, Field<NumericUpDown>(panel, "pitchCorrection").Maximum);
          Assert.Equal(-500, Field<NumericUpDown>(panel, "pitchCorrection").Minimum);

          using var image = new Bitmap(200, 120);
          using (var g = Graphics.FromImage(image)) g.Clear(Color.CornflowerBlue);
          Invoke(panel, "SetTxImage", new Bitmap(image));
          Assert.Equal(1, tabs.SelectedIndex);
          Assert.NotNull(Field<PictureBox>(panel, "txPicture").Image);
          var template = Field<ComboBox>(panel, "templateBox");
          template.SelectedItem = "QSL 卡片";
          Field<TextBox>(panel, "overlayText").Text = "CQ SSTV {UTC}";
          Invoke(panel, "RotateImage");
          Invoke(panel, "UndoImage");
          Invoke(panel, "RedoImage");
          Invoke(panel, "CropImage");
          Assert.NotNull(Field<PictureBox>(panel, "txPicture").Image);

          var mode = Field<ComboBox>(panel, "modeBox");
          mode.SelectedItem = SstvTxModeProfile.All.Single(x => x.Name == "P3");
          var preview = Field<PictureBox>(panel, "txPicture").Image;
          Assert.NotNull(preview);
          Assert.Equal(640, preview!.Width);
          Assert.Equal(496, preview.Height);

          var receive = tabs.TabPages[0];
          Assert.Contains(receive.Controls.OfType<SstvToneSpectrumView>(),
            view => view.Height >= 90);
          tabs.SelectedIndex = 3;
          Assert.Equal("通联日志", tabs.SelectedTab?.Text);
        }
        catch (Exception ex) { failure = ex; }
      });
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "STA UI smoke timed out");
      if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ReceiverFrequencyRulerCanDrawAtMinimumWindowSize()
    {
      Exception? failure = null;
      var thread = new Thread(() =>
      {
        try
        {
          using var view = new SstvToneSpectrumView
          {
            Size = new Size(240, 90)
          };
          var audio = new float[2048];
          for (int i = 0; i < audio.Length; i++)
            audio[i] = (float)Math.Sin(2 * Math.PI * 1900 * i / 48000);
          view.SetLevels(SstvToneSpectrum.Measure(audio));
          using var picture = new Bitmap(240, 90);
          view.DrawToBitmap(picture, new Rectangle(0, 0, 240, 90));
          Assert.Equal(255, picture.GetPixel(5, 5).A);
        }
        catch (Exception ex) { failure = ex; }
      });
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Spectrum paint timed out");
      if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class UiFixture : IDisposable
    {
      private readonly Context context = new();
      public readonly SstvConsolePanel Panel;
      public UiFixture() => Panel = new SstvConsolePanel(context);
      public void Dispose() => Panel.Close();
    }
  }
}
