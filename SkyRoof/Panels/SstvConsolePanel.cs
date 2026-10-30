using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkyRoof.SSTV;
using VE3NEA;
using NAudio.CoreAudioApi;
using VE3NEA.SkySSTV;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// Operator-facing SSTV picture workspace. The RX image source is the
  /// existing satellite decoder; WAV export is explicitly offline and never
  /// invokes a radio PTT/CAT API. Retains the rest of SkyRoof unchanged.
  /// </summary>
  public sealed class SstvConsolePanel : DockContent
  {
    private readonly Context ctx;
    private readonly SstvAudioReceiveSession receiveSession = new();
    private readonly ComboBox rxSourceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 152
    };
    private readonly ComboBox rxDeviceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 280
    };
    private readonly Button rxToggleButton = new()
    {
      Text = "开始接收", Width = 96, Height = 30,
      Margin = new Padding(4)
    };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly PictureBox rxPicture = new()
    {
      Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 33, 40),
      SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly PictureBox txPicture = new()
    {
      Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 33, 40),
      SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly ComboBox modeBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 145
    };
    private readonly TextBox overlayText = new() { Width = 165 };
    private readonly ListBox archiveList = new() { Dock = DockStyle.Fill };
    private readonly Label rxStatus = new()
    {
      Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleLeft,
      Text = "等待接收图像"
    };
    private readonly Label txStatus = new()
    {
      Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft,
      Text = "选择图片和模式，即可保存 SSTV 音频"
    };
    private Bitmap? txBase;
    private bool closed;

    public SstvConsolePanel(Context ctx)
    {
      this.ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
      ctx.SstvConsolePanel = this;
      Text = "SSTV";
      TabText = "SSTV";
      Width = 980;
      Height = 720;
      MinimumSize = new Size(650, 420);
      Controls.Add(tabs);
      BuildRxTab();
      BuildTxTab();
      BuildHistoryTab();
      foreach (SstvModeSpec mode in SstvModes.All)
        modeBox.Items.Add(mode);
      modeBox.DisplayMember = nameof(SstvModeSpec.Name);
      if (modeBox.Items.Count > 0) modeBox.SelectedIndex = 0;
      modeBox.SelectedIndexChanged += (_, _) => RefreshTxPreview();
      overlayText.Text = ctx.Settings.User.Call ?? "";
      rxSourceBox.Items.AddRange(new object[]
      {
        "声卡 AF", "RS-BA1 回环 AF", "声卡 12 kHz IF", "回环 12 kHz IF"
      });
      rxSourceBox.SelectedIndexChanged += (_, _) => PopulateAudioDevices();
      rxSourceBox.SelectedIndex = 0;
      rxToggleButton.Click += (_, _) => ToggleReceive();
      receiveSession.ImageAvailable += OnAudioReceived;
      overlayText.TextChanged += (_, _) => RefreshTxPreview();
      archiveList.DoubleClick += (_, _) => ShowSelectedHistory();
      ReloadHistory();
    }

    private static Button Btn(string text, Action action, int width = 95)
    {
      var b = new Button
      {
        Text = text, Width = width, Height = 30,
        Margin = new Padding(4, 4, 4, 4)
      };
      b.Click += (_, _) => action();
      return b;
    }

    private static FlowLayoutPanel Toolbar() => new()
    {
      Dock = DockStyle.Top, Height = 43, WrapContents = false,
      AutoScroll = true, Padding = new Padding(6, 2, 6, 2)
    };

    private void BuildRxTab()
    {
      var page = new TabPage("接收 RX");
      var bar = Toolbar();
      bar.Controls.Add(rxSourceBox);
      bar.Controls.Add(rxDeviceBox);
      bar.Controls.Add(rxToggleButton);
      bar.Controls.Add(Btn("保存图片", SaveReceivedImage));
      bar.Controls.Add(Btn("复制图片", () =>
      {
        if (rxPicture.Image != null) Clipboard.SetImage(rxPicture.Image);
      }));
      bar.Controls.Add(Btn("接收记录", () => { ReloadHistory(); tabs.SelectedIndex = 2; }));
      page.Controls.Add(rxPicture);
      page.Controls.Add(rxStatus);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void BuildTxTab()
    {
      var page = new TabPage("发送 TX");
      var bar = Toolbar();
      bar.Controls.Add(Btn("打开图片", LoadImage));
      bar.Controls.Add(Btn("粘贴", PasteImage, 65));
      bar.Controls.Add(new Label
      {
        Text = "模式", Width = 36, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(modeBox);
      bar.Controls.Add(new Label
      {
        Text = "文字", Width = 36, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(overlayText);
      bar.Controls.Add(Btn("保存音频 WAV", () => _ = ExportWaveAsync(), 116));
      page.Controls.Add(txPicture);
      page.Controls.Add(txStatus);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void BuildHistoryTab()
    {
      var page = new TabPage("图库 / 历史");
      var bar = Toolbar();
      bar.Controls.Add(Btn("刷新", ReloadHistory));
      bar.Controls.Add(Btn("查看", ShowSelectedHistory));
      bar.Controls.Add(Btn("用作发送图片", UseSelectedHistoryForTx, 135));
      page.Controls.Add(archiveList);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void PopulateAudioDevices()
    {
      rxDeviceBox.Items.Clear();
      try
      {
        bool loopback = rxSourceBox.SelectedIndex is 1 or 3;
        foreach (AudioDeviceEntry d in Soundcard.ListDevices(
          loopback ? DataFlow.Render : DataFlow.Capture))
          rxDeviceBox.Items.Add(new DeviceEntry(d.Id, d.Name));
        if (rxDeviceBox.Items.Count > 0) rxDeviceBox.SelectedIndex = 0;
      }
      catch (Exception ex)
      {
        rxStatus.Text = "无法列出音频设备: " + ex.Message;
      }
    }

    private void ToggleReceive()
    {
      if (receiveSession.IsRunning)
      {
        receiveSession.Stop();
        rxToggleButton.Text = "开始接收";
        rxStatus.Text = "接收已停止";
        return;
      }
      if (rxDeviceBox.SelectedItem is not DeviceEntry device)
      {
        MessageBox.Show(this, "请选择接收音频设备。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      try
      {
        receiveSession.Start((SstvReceiveSource)rxSourceBox.SelectedIndex, device.Id);
        rxToggleButton.Text = "停止接收";
        rxStatus.Text = "正在等待 SSTV 信号...";
      }
      catch (Exception ex)
      {
        receiveSession.Stop();
        MessageBox.Show(this, ex.Message, "无法启动接收",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void OnAudioReceived(SstvImageEvent evt)
    {
      if (closed || !IsHandleCreated ||
          (!evt.Final && evt.ValidRows % 12 != 0)) return;
      Bitmap picture;
      try { picture = evt.Image.ToBitmap(); }
      catch { return; }
      try
      {
        BeginInvoke((Action)(() =>
        {
          using (picture)
          {
            if (!closed && !IsDisposed)
              ShowReceivedSstv(picture, evt.Mode.ToString(), evt.Final, null);
          }
        }));
      }
      catch (InvalidOperationException) { picture.Dispose(); }
      catch (ObjectDisposedException) { picture.Dispose(); }
    }

    private void LoadImage()
    {
      using var dialog = new OpenFileDialog
      {
        Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
        Title = "选择发送图片"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        using var loaded = Image.FromFile(dialog.FileName);
        SetTxImage(new Bitmap(loaded));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法打开图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void PasteImage()
    {
      try
      {
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is Image image)
          SetTxImage(new Bitmap(image));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法粘贴图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void SetTxImage(Bitmap image)
    {
      var old = txBase;
      txBase = image;
      old?.Dispose();
      RefreshTxPreview();
      tabs.SelectedIndex = 1;
    }

    private Bitmap? RenderTxPreview()
    {
      if (txBase == null) return null;
      SstvModeSpec spec = modeBox.SelectedItem as SstvModeSpec ?? SstvModes.All[0];
      var canvas = new Bitmap(spec.Width, spec.Height, PixelFormat.Format32bppArgb);
      using var g = Graphics.FromImage(canvas);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.Clear(Color.Black);
      g.DrawImage(txBase, new Rectangle(0, 0, canvas.Width, canvas.Height));
      if (!string.IsNullOrWhiteSpace(overlayText.Text))
      {
        float fontSize = Math.Max(12f, canvas.Width / 19f);
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold);
        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var foreground = new SolidBrush(Color.White);
        float x = Math.Max(8, canvas.Width * .035f);
        float y = Math.Max(6, canvas.Height - fontSize * 1.75f);
        g.DrawString(overlayText.Text, font, shadow, x + 2, y + 2);
        g.DrawString(overlayText.Text, font, foreground, x, y);
      }
      return canvas;
    }

    private void RefreshTxPreview()
    {
      if (closed) return;
      var newPicture = RenderTxPreview();
      var old = txPicture.Image;
      txPicture.Image = newPicture;
      old?.Dispose();
    }

    private async Task ExportWaveAsync()
    {
      if (txPicture.Image is not Bitmap preview ||
          modeBox.SelectedItem is not SstvModeSpec spec)
      {
        MessageBox.Show(this, "请先打开或粘贴图片。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      using var save = new SaveFileDialog
      {
        Title = "保存 SSTV 音频",
        Filter = "WAV 音频文件|*.wav",
        FileName = $"SSTV_{spec.Name.Replace(' ', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.wav"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      using var snapshot = new Bitmap(preview);
      txStatus.Text = "正在生成 SSTV 音频...";
      try
      {
        await Task.Run(() => SstvWaveExporter.Export(snapshot, spec.Mode, save.FileName));
        if (!closed) txStatus.Text = "音频已保存";
      }
      catch (Exception ex)
      {
        if (!closed)
        {
          txStatus.Text = "保存失败";
          MessageBox.Show(this, ex.Message, "SSTV",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      }
    }

    /// <summary>Called by TelemetryPanel on the WinForms UI thread.</summary>
    public void ShowReceivedSstv(Bitmap picture, string mode, bool final, string? savedFile)
    {
      if (closed || IsDisposed || picture == null) return;
      var next = new Bitmap(picture);
      var old = rxPicture.Image;
      rxPicture.Image = next;
      old?.Dispose();
      rxStatus.Text = final ? $"接收完成 · {mode}" : $"正在接收 · {mode}";
      if (final && !string.IsNullOrEmpty(savedFile)) ReloadHistory();
    }

    private void SaveReceivedImage()
    {
      if (rxPicture.Image == null) return;
      using var save = new SaveFileDialog
      {
        Filter = "PNG 图片|*.png",
        FileName = $"SSTV_{DateTime.Now:yyyyMMdd_HHmmss}.png"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      rxPicture.Image.Save(save.FileName, ImageFormat.Png);
    }

    private void ReloadHistory()
    {
      if (closed) return;
      archiveList.BeginUpdate();
      try
      {
        archiveList.Items.Clear();
        string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
        if (!Directory.Exists(folder)) return;
        foreach (string path in Directory.EnumerateFiles(folder, "*.png")
          .OrderByDescending(File.GetLastWriteTimeUtc).Take(64))
          archiveList.Items.Add(new HistoryImage(path));
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
      finally { archiveList.EndUpdate(); }
    }

    private void ShowSelectedHistory()
    {
      if (archiveList.SelectedItem is not HistoryImage item) return;
      try
      {
        using var loaded = Image.FromFile(item.Path);
        using var preview = new Bitmap(loaded);
        ShowReceivedSstv(preview, "历史记录", true, null);
        tabs.SelectedIndex = 0;
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法读取图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void UseSelectedHistoryForTx()
    {
      if (archiveList.SelectedItem is not HistoryImage item) return;
      try
      {
        using var loaded = Image.FromFile(item.Path);
        SetTxImage(new Bitmap(loaded));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法读取图片",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private sealed record DeviceEntry(string Id, string Name)
    {
      public override string ToString() => Name;
    }

    private sealed record HistoryImage(string Path)
    {
      public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
      closed = true;
      receiveSession.Dispose();
      if (ctx.SstvConsolePanel == this) ctx.SstvConsolePanel = null;
      var oldRx = rxPicture.Image;
      rxPicture.Image = null;
      oldRx?.Dispose();
      var oldTx = txPicture.Image;
      txPicture.Image = null;
      oldTx?.Dispose();
      txBase?.Dispose();
      base.OnFormClosed(e);
    }
  }
}
