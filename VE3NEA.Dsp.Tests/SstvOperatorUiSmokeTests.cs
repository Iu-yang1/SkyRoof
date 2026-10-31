using System;
using System.Drawing;
using System.IO;
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
          panel.Show();
          Application.DoEvents();
          var tabs = panel.Controls.OfType<TabControl>().Single();
          Assert.True(panel.Visible);
          Assert.True(tabs.Visible);
          Assert.Equal(new[] { "接收 RX", "发送 TX",
              "图库 / 历史", "通联日志" },
            tabs.TabPages.Cast<TabPage>().Select(x => x.Text).ToArray());
          Assert.Equal(43, Field<ComboBox>(panel, "modeBox").Items.Count);
          Assert.False(Field<CheckBox>(panel, "txApproval").Checked);
          Assert.False(Field<Button>(panel, "txStopButton").Enabled);
          Assert.False(Field<Button>(panel, "txSendButton").Enabled);
          Assert.True(Field<CheckBox>(panel, "autoVisCorrection").Checked);
          Assert.Equal(500m, Field<NumericUpDown>(panel, "pitchCorrection").Maximum);
          Assert.Equal(-500m, Field<NumericUpDown>(panel, "pitchCorrection").Minimum);

          using var image = new Bitmap(200, 120);
          using (var g = Graphics.FromImage(image)) g.Clear(Color.CornflowerBlue);
          Invoke(panel, "SetTxImage", new Bitmap(image));
          Assert.Equal(1, tabs.SelectedIndex);
          Assert.NotNull(Field<PictureBox>(panel, "txPicture").Image);
          Assert.False(Field<Button>(panel, "txSendButton").Enabled,
            "an imported image must never silently arm RF PTT");
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
          // Render actual production UI surfaces into CI attachments.
          // This confirms a real WinForms paint path without requiring
          // any connected transceiver or a simulated RF key-up.
          SaveLayoutSnapshot(panel, tabs, 1, "tx-980x720", 980, 720);
          SaveLayoutSnapshot(panel, tabs, 1, "tx-650x420", 650, 420);
          SaveLayoutSnapshot(panel, tabs, 0, "rx-980x720", 980, 720);
          SaveLayoutSnapshot(panel, tabs, 0, "rx-650x420", 650, 420);

          var receive = tabs.TabPages[0];
          Assert.Contains(receive.Controls.OfType<SstvToneSpectrumView>(),
            view => view.Height >= 90);
          var rxPicture = Field<PictureBox>(panel, "rxPicture");
          rxPicture.Tag = "old-image.png";
          panel.ShowReceivedSstv(image, "Martin 1", false, null);
          Assert.Null(rxPicture.Tag);
          Assert.Contains("正在接收", Field<Label>(panel, "rxStatus").Text);
          panel.ShowReceivedSstv(image, "Martin 1", true, null);
          Assert.Equal("Martin 1", Field<string>(panel, "lastReceivedMode"));
          Assert.Null(rxPicture.Tag);
          Assert.Contains("接收完成", Field<Label>(panel, "rxStatus").Text);
          tabs.SelectedIndex = 3;
          Assert.Equal("通联日志", tabs.SelectedTab?.Text);
        }
        catch (Exception ex) { failure = ex; }
      });
      thread.IsBackground = true;
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
      thread.IsBackground = true;
      thread.SetApartmentState(ApartmentState.STA);
      thread.Start();
      Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Spectrum paint timed out");
      if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void SaveLayoutSnapshot(SstvConsolePanel panel,
      TabControl tabs, int tab, string name, int width, int height)
    {
      string? dir = Environment.GetEnvironmentVariable(
        "SKYROOF_SSTV_UI_ARTIFACT_DIR");
      if (string.IsNullOrWhiteSpace(dir)) return;
      Directory.CreateDirectory(dir);
      panel.Size = new Size(width, height);
      panel.PerformLayout();
      tabs.SelectedIndex = tab;
      tabs.PerformLayout();
      Application.DoEvents();
      using var screen = new Bitmap(width, height);
      panel.DrawToBitmap(screen, new Rectangle(0, 0, width, height));
      // A prior CI run reported a nominally successful capture that was
      // actually an empty DockContent frame. Reject such false acceptance.
      int darkPixels = 0;
      for (int y = 35; y < height; y += 3)
        for (int x = 10; x < width - 10; x += 3)
        {
          Color pixel = screen.GetPixel(x, y);
          if (pixel.R < 100 && pixel.G < 115 && pixel.B < 135)
            darkPixels++;
        }
      Assert.True(darkPixels > 300,
        $"SSTV {name} screenshot has no rendered RX/TX content");
      screen.Save(Path.Combine(dir, name + ".png"));
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
