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
          var rxModes = Field<ComboBox>(panel, "rxModeBox");
          Assert.Equal(44, rxModes.Items.Count); // auto + every TX mode
          Assert.Equal(
            Field<ComboBox>(panel, "modeBox").Items.Cast<SstvTxModeProfile>()
              .Select(profile => profile.Name).ToArray(),
            rxModes.Items.Cast<object>().Skip(1).Cast<SstvTxModeProfile>()
              .Select(profile => profile.Name).ToArray());
          Assert.Contains(rxModes.Items.Cast<object>(),
            item => item is SstvTxModeProfile profile && profile.Name == "Robot 36");
          Assert.Contains(rxModes.Items.Cast<object>(),
            item => item is SstvTxModeProfile profile && profile.Name == "MC110-N");
          Assert.False(Field<CheckBox>(panel, "txApproval").Checked);
          Assert.False(Field<Button>(panel, "txStopButton").Enabled);
          Assert.False(Field<Button>(panel, "txSendButton").Enabled);
          Assert.Equal(-1, Field<ComboBox>(panel, "txOutputBox").SelectedIndex);
          Assert.True(Field<CheckBox>(panel, "autoVisCorrection").Checked);
          Assert.Equal(500m, Field<NumericUpDown>(panel, "pitchCorrection").Maximum);
          Assert.Equal(-500m, Field<NumericUpDown>(panel, "pitchCorrection").Minimum);

          using var image = new Bitmap(200, 120);
          using (var g = Graphics.FromImage(image)) g.Clear(Color.CornflowerBlue);
          Invoke(panel, "SetTxImage", new Bitmap(image));
          Assert.Equal(1, tabs.SelectedIndex);
          Assert.NotNull(Field<PictureBox>(panel, "txPicture").Image);
          Assert.Contains("图片已加载", Field<Label>(panel, "txStatus").Text);
          Assert.False(Field<Button>(panel, "txSendButton").Enabled,
            "an imported image must never silently arm RF PTT");
          var template = Field<ComboBox>(panel, "templateBox");
          template.SelectedItem = "QSL 卡片";
          Field<TextBox>(panel, "overlayText").Text = "CQ SSTV {UTC}";
          // Exercise actual WinForms Click events, not only private
          // methods. This catches broken button wiring and disabled controls.
          Button Action(string text) =>
            tabs.TabPages[1].Controls.OfType<FlowLayoutPanel>()
              .SelectMany(row => row.Controls.OfType<Button>())
              .Single(button => button.Text == text);
          Action("旋转").PerformClick();
          Assert.Equal(120, Field<Bitmap>(panel, "txBase").Width);
          Action("撤销").PerformClick();
          Assert.Equal(200, Field<Bitmap>(panel, "txBase").Width);
          Action("重做").PerformClick();
          Assert.Equal(120, Field<Bitmap>(panel, "txBase").Width);
          Action("裁剪").PerformClick();
          Assert.Equal(120, Field<Bitmap>(panel, "txBase").Width);
          Assert.True(Field<Bitmap>(panel, "txBase").Height <= 120);
          Assert.NotNull(Field<PictureBox>(panel, "txPicture").Image);

          // Three independent TX text layers with per-layer size controls.
          var myCall = Field<TextBox>(panel, "myCallInput");
          var theirCall = Field<TextBox>(panel, "theirCallInput");
          var freeText = Field<TextBox>(panel, "overlayText");
          var activeLayer = Field<ComboBox>(panel, "textLayerBox");
          var textSize = Field<NumericUpDown>(panel, "textSizeBox");
          Assert.Equal(3, activeLayer.Items.Count);
          myCall.Text = "BG5JSU";
          theirCall.Text = "HB9Q";
          freeText.Text = "QSO {HISCALL}";
          activeLayer.SelectedIndex = 0;
          textSize.Value = 11;
          Assert.Equal(11f, Field<SstvTextLayer[]>(panel, "textLayers")[0].SizePercent);
          activeLayer.SelectedIndex = 1;
          textSize.Value = 8;
          Assert.Equal(8f, Field<SstvTextLayer[]>(panel, "textLayers")[1].SizePercent);
          Assert.Contains(Action("删除模板").Text, "删除模板");

          var mode = Field<ComboBox>(panel, "modeBox");
          mode.SelectedItem = SstvTxModeProfile.All.Single(x => x.Name == "P3");
          var preview = Field<PictureBox>(panel, "txPicture").Image;
          Assert.NotNull(preview);
          Assert.Equal(640, preview!.Width);
          Assert.Equal(496, preview.Height);
          // Test picture hit-testing: dragging text changes only the chosen layer.
          activeLayer.SelectedIndex = 0;
          var previewBox = Field<PictureBox>(panel, "txPicture");
          var letterBounds = Field<RectangleF[]>(panel, "textBounds")[0];
          Assert.False(letterBounds.IsEmpty);
          RectangleF clientBounds = (RectangleF)typeof(SstvConsolePanel)
            .GetMethod("TxPictureClientBounds",
              BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, null)!;
          float zoom = clientBounds.Width / preview!.Width;
          int px = (int)(clientBounds.X + (letterBounds.X + 2) * zoom);
          int py = (int)(clientBounds.Y + (letterBounds.Y + 2) * zoom);
          float priorX = Field<SstvTextLayer[]>(panel, "textLayers")[0].X;
          Invoke(panel, "OnTxPictureMouseDown", previewBox,
            new MouseEventArgs(MouseButtons.Left, 1, px, py, 0));
          Invoke(panel, "OnTxPictureMouseMove", previewBox,
            new MouseEventArgs(MouseButtons.Left, 1, px + 24, py, 0));
          Assert.NotEqual(priorX, Field<SstvTextLayer[]>(panel, "textLayers")[0].X);
          Assert.Equal(8f, Field<SstvTextLayer[]>(panel, "textLayers")[1].SizePercent);
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

          // ESC should immediately cancel the pending SSTV operation,
          // update the status and disable repeated STOP activation.
          using (var virtualSend = new CancellationTokenSource())
          {
            typeof(SstvConsolePanel).GetField("txOperationStop",
              BindingFlags.NonPublic | BindingFlags.Instance)!
              .SetValue(panel, virtualSend);
            var stopButton = Field<Button>(panel, "txStopButton");
            stopButton.Enabled = true;
            typeof(SstvConsolePanel).GetMethod("OnKeyDown",
              BindingFlags.NonPublic | BindingFlags.Instance)!
              .Invoke(panel, new object[] { new KeyEventArgs(Keys.Escape) });
            Assert.True(virtualSend.IsCancellationRequested);
            Assert.False(stopButton.Enabled);
            Assert.Contains("正在停止", Field<Label>(panel, "txStatus").Text);
            typeof(SstvConsolePanel).GetField("txOperationStop",
              BindingFlags.NonPublic | BindingFlags.Instance)!
              .SetValue(panel, null);
          }

          // Simulate a prior OFF timeout without touching any real CAT device.
          // Merely ticking operator approval must not rearm an unknown PTT.
          var txSession = Field<SstvTransmitSession>(panel, "transmitSession");
          typeof(SstvTransmitSession).GetField("releaseUnconfirmed",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(txSession, 1);
          var approval = Field<CheckBox>(panel, "txApproval");
          approval.Checked = true;
          Invoke(panel, "UpdateTxAvailability");
          Assert.False(approval.Checked);
          Assert.False(Field<Button>(panel, "txSendButton").Enabled);
          Assert.Equal(SstvTxPolicy.PttReleaseUnconfirmedMessage,
            Field<Label>(panel, "txStatus").Text);
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
      // No CAT/PTT or PaletteManager is needed for pure UI testing.
      // A full Context constructor writes shared palette fixture files,
      // racing unrelated GPU tests when xUnit runs in parallel.
      private readonly Context context;
      public readonly SstvConsolePanel Panel;
      public UiFixture()
      {
        context = (Context)System.Runtime.CompilerServices.RuntimeHelpers
          .GetUninitializedObject(typeof(Context));
        context.Settings = new SkyRoof.Settings();
        Panel = new SstvConsolePanel(context);
      }
      public void Dispose() => Panel.Close();
    }
  }
}
