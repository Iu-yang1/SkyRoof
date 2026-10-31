using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
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
    private readonly SstvTransmitSession transmitSession;
    private readonly ComboBox txOutputBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 245
    };
    private readonly CheckBox txCwId = new()
    {
      Text = "CW ID", AutoSize = true,
      Padding = new Padding(2, 7, 2, 0)
    };
    private readonly CheckBox txFskId = new()
    {
      Text = "FSK ID", AutoSize = true,
      Padding = new Padding(2, 7, 2, 0)
    };
    private readonly CheckBox txApproval = new()
    {
      Text = "已确认频率 / 功率", AutoSize = true,
      Padding = new Padding(3, 7, 3, 0)
    };
    private readonly Button txSendButton = new()
    {
      Text = "发射图片", Width = 90, Height = 30, Margin = new Padding(4),
      Enabled = false
    };
    private readonly Button txStopButton = new()
    {
      Text = "停止发射", Width = 90, Height = 30, Margin = new Padding(4),
      Enabled = false
    };
    private readonly ComboBox rxModeBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 178
    };
    private readonly ComboBox rxSourceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 152
    };
    private readonly ComboBox rxDeviceBox = new()
    {
      DropDownStyle = ComboBoxStyle.DropDownList, Width = 280
    };
    private readonly CheckBox autoVisCorrection = new()
    {
      Text = "自动校频", AutoSize = true, Checked = true,
      Padding = new Padding(3, 7, 3, 0)
    };
    private readonly NumericUpDown pitchCorrection = new()
    {
      Minimum = -500, Maximum = 500, Increment = 5,
      DecimalPlaces = 0, Width = 72, Height = 30
    };
    private readonly NumericUpDown imageSlantCorrection = new()
    {
      Minimum = -1000, Maximum = 1000, Increment = 25,
      DecimalPlaces = 0, Width = 76, Height = 30
    };
    private readonly Button rxToggleButton = new()
    {
      Text = "开始接收", Width = 96, Height = 30,
      Margin = new Padding(4)
    };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly SstvToneSpectrumView rxSpectrum = new()
    {
      Dock = DockStyle.Bottom,
      Height = 106
    };
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
    private readonly TextBox overlayText = new()
    {
      Width = 185, MaxLength = 512,
      PlaceholderText = "呼号 / {MYGRID} / {SAT} / {UTC}"
    };
    private readonly ComboBox templateBox = new()
    {
      Width = 110, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Stack<Bitmap> undoImages = new();
    private readonly Stack<Bitmap> redoImages = new();
    private const int HistoryLimit = 10;
    private readonly ListBox archiveList = new() { Dock = DockStyle.Fill };
    private readonly ListBox qsoList = new() { Dock = DockStyle.Fill };
    private readonly TextBox qsoCall = new() { Width = 130 };
    private readonly TextBox qsoNotes = new() { Width = 250 };
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
    private CancellationTokenSource? replayStop;
    private CancellationTokenSource? txOperationStop;
    private Bitmap? txBase;
    private string? lastReceivedMode;
    private bool closed;

    public SstvConsolePanel(Context ctx)
    {
      this.ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
      ctx.SstvConsolePanel = this;
      transmitSession = new SstvTransmitSession(ctx);
      Text = "SSTV";
      TabText = "SSTV";
      Width = 980;
      Height = 720;
      MinimumSize = new Size(650, 420);
      Controls.Add(tabs);
      BuildRxTab();
      BuildTxTab();
      BuildHistoryTab();
      BuildQsoTab();
      foreach (SstvTxModeProfile mode in SstvTxModeProfile.All)
        modeBox.Items.Add(mode);
      modeBox.DisplayMember = nameof(SstvTxModeProfile.Name);
      if (modeBox.Items.Count > 0) modeBox.SelectedIndex = 0;
      modeBox.SelectedIndexChanged += (_, _) => RefreshTxPreview();
      overlayText.Text = ctx.Settings.User.Call ?? "";
      rxSourceBox.Items.AddRange(new object[]
      {
        "声卡 AF", "RS-BA1 回环 AF", "声卡 12 kHz IF", "回环 12 kHz IF"
      });
      rxSourceBox.SelectedIndexChanged += (_, _) => PopulateAudioDevices();
      rxSourceBox.SelectedIndex = 0;
      rxModeBox.Items.Add("自动识别 SSTV");
      foreach (SstvTxModeProfile profile in SstvTxModeProfile.All)
        if (profile.Native == null) rxModeBox.Items.Add(profile);
      rxModeBox.SelectedIndex = 0;
      rxToggleButton.Click += (_, _) => ToggleReceive();
      pitchCorrection.ValueChanged += (_, _) =>
        receiveSession.ManualCorrectionHz = (int)pitchCorrection.Value;
      autoVisCorrection.CheckedChanged += (_, _) =>
        receiveSession.AutoVisEnabled = autoVisCorrection.Checked;
      receiveSession.ImageAvailable += OnAudioReceived;
      receiveSession.RasterImageAvailable += OnRasterReceived;
      receiveSession.VisSynchronized += OnVisSynchronized;
      receiveSession.SpectrumAvailable += OnSpectrumAvailable;
      receiveSession.FskStationIdentified += OnLiveFskStationIdentified;
      overlayText.TextChanged += (_, _) => RefreshTxPreview();
      qsoCall.TextChanged += (_, _) => RefreshTxPreview();
      templateBox.Items.AddRange(new object[]
      {
        "经典呼号", "QSL 卡片", "卫星横幅", "纯图片"
      });
      templateBox.SelectedIndex = 0;
      templateBox.SelectedIndexChanged += (_, _) =>
      {
        if (templateBox.SelectedItem is SstvUserTemplate selected)
          overlayText.Text = selected.OverlayText;
        RefreshTxPreview();
      };
      ReloadUserTemplates();
      archiveList.DoubleClick += (_, _) => ShowSelectedHistory();
      txSendButton.Click += (_, _) => _ = SendImageAsync();
      txApproval.CheckedChanged += (_, _) => UpdateTxAvailability();
      // A changed audio route invalidates the operator's previous approval.
      txOutputBox.SelectedIndexChanged += (_, _) =>
      {
        txApproval.Checked = false;
        UpdateTxAvailability();
      };
      txStopButton.Click += (_, _) => StopTransmit();

      PopulateTxOutputDevices();
      ReloadHistory();
      ReloadQsoLog();
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
      AutoScroll = false, Padding = new Padding(6, 2, 6, 2)
    };

    private void BuildRxTab()
    {
      var page = new TabPage("接收 RX");
      var bar = Toolbar();
      bar.Controls.Add(rxSourceBox);
      bar.Controls.Add(rxDeviceBox);
      bar.Controls.Add(rxToggleButton);
      var tuning = Toolbar();
      tuning.Controls.Add(rxModeBox);
      tuning.Controls.Add(new Label
      {
        Text = "校频 Hz", Width = 57, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      tuning.Controls.Add(pitchCorrection);
      tuning.Controls.Add(autoVisCorrection);
      tuning.Controls.Add(new Label
      {
        Text = "图像倾斜", Width = 66, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      tuning.Controls.Add(imageSlantCorrection);
      var actions = Toolbar();
      actions.Controls.Add(Btn("导入 WAV", () => _ = ReplayWavAsync()));
      actions.Controls.Add(Btn("手动开始", ManualStartReceive, 98));
      actions.Controls.Add(Btn("保存图片", SaveReceivedImage));
      actions.Controls.Add(Btn("复制图片", () =>
      {
        if (rxPicture.Image != null) Clipboard.SetImage(rxPicture.Image);
      }));
      actions.Controls.Add(Btn("接收记录", () => { ReloadHistory(); tabs.SelectedIndex = 2; }));
      page.Controls.Add(rxPicture);
      page.Controls.Add(rxSpectrum);
      page.Controls.Add(rxStatus);
      page.Controls.Add(actions);
      page.Controls.Add(tuning);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void BuildTxTab()
    {
      var page = new TabPage("发送 TX");
      var bar = Toolbar();
      bar.Controls.Add(Btn("打开图片", LoadImage));
      bar.Controls.Add(Btn("粘贴", PasteImage, 65));
      bar.Controls.Add(Btn("撤销", UndoImage, 60));
      bar.Controls.Add(Btn("重做", RedoImage, 60));
      bar.Controls.Add(Btn("旋转", RotateImage, 60));
      bar.Controls.Add(Btn("镜像", MirrorImage, 60));
      bar.Controls.Add(Btn("裁剪", CropImage, 60));
      var format = Toolbar();
      format.Controls.Add(new Label
      {
        Text = "模式", Width = 36, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      format.Controls.Add(modeBox);
      format.Controls.Add(templateBox);
      format.Controls.Add(Btn("保存模板", SaveUserTemplate, 95));
      format.Controls.Add(Btn("保存音频 WAV", () => _ = ExportWaveAsync(), 116));
      var overlay = Toolbar();
      overlay.Controls.Add(new Label
      {
        Text = "文字", Width = 36, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      overlay.Controls.Add(overlayText);
      var transmit = Toolbar();
      transmit.Controls.Add(new Label
      {
        Text = "发射声卡", Width = 66, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      transmit.Controls.Add(txOutputBox);
      transmit.Controls.Add(txCwId);
      transmit.Controls.Add(txFskId);
      var txActions = Toolbar();
      txActions.Controls.Add(txApproval);
      txActions.Controls.Add(txSendButton);
      txActions.Controls.Add(txStopButton);
      page.Controls.Add(txPicture);
      page.Controls.Add(txStatus);
      page.Controls.Add(txActions);
      page.Controls.Add(transmit);
      page.Controls.Add(overlay);
      page.Controls.Add(format);
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

    private async Task ReplayWavAsync()
    {
      if (replayStop != null)
      {
        replayStop.Cancel();
        return;
      }
      using var dialog = new OpenFileDialog
      {
        Filter = "WAV 音频文件|*.wav",
        Title = "打开 SSTV 录音"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      receiveSession.Stop();
      rxToggleButton.Text = "开始接收";
      var stop = new CancellationTokenSource();
      replayStop = stop;
      string? normalizedTemporaryFile = null;
      try
      {
        rxStatus.Text = "正在准备 SSTV 录音...";
        string workingWave = dialog.FileName;
        bool convert = await Task.Run(
          () => SstvWavNormalizer.NeedsConversion(dialog.FileName), stop.Token);
        if (convert)
        {
          normalizedTemporaryFile = Path.Combine(Path.GetTempPath(),
            $"SkyRoof_SstvRx_{Guid.NewGuid():N}.wav");
          string output = normalizedTemporaryFile;
          await Task.Run(() => SstvWavNormalizer.Normalize(
            dialog.FileName, output, stop.Token), stop.Token);
          workingWave = output;
        }
        stop.Token.ThrowIfCancellationRequested();
        rxStatus.Text = "正在解析 SSTV 录音...";
        double clockPpm = (double)imageSlantCorrection.Value;
        SstvVisModeIdentity? identified = await Task.Run(
          () => SstvVisModeIdentifier.DetectWave(workingWave), stop.Token);
        SstvTxModeProfile? manual = rxModeBox.SelectedItem as SstvTxModeProfile;
        SstvTxModeProfile? resolved = manual ?? identified?.Profile;
        if (resolved != null && resolved.Native == null)
        {
          using Bitmap image = await Task.Run(
            () => SstvOfflineRasterDecoder.Decode(
              workingWave, resolved,
              identified?.Profile.Name == resolved.Name
                ? identified.Value.LeadingAudio : default,
              identified?.Profile.Name == resolved.Name
                ? identified.Value.FrequencyOffsetHz : 0,
              clockPpm), stop.Token);
          if (!closed)
          {
            string gallery = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
            Directory.CreateDirectory(gallery);
            string name = $"SSTV_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{resolved.Name.Replace(' ', '_')}";
            string pictureFile = Path.Combine(gallery, name + ".png");
            image.Save(pictureFile, ImageFormat.Png);
            File.WriteAllText(Path.Combine(gallery, name + ".json"),
              System.Text.Json.JsonSerializer.Serialize(new
              {
                Utc = DateTime.UtcNow,
                Source = "导入的 WAV",
                Mode = resolved.Name,
                Picture = Path.GetFileName(pictureFile)
              }));
            ShowReceivedSstv(image, resolved.Name, true, pictureFile);
            await IdentifyRecordedFskIdAsync(workingWave, stop.Token);
          }
          return;
        }
        await SstvWaveReceiver.DecodeAsync(workingWave, OnAudioReceived,
          progress =>
          {
            if (closed || !IsHandleCreated) return;
            try
            {
              BeginInvoke((Action)(() =>
              {
                if (!closed)
                  rxStatus.Text = $"正在解析 SSTV 录音 · {progress:P0}";
              }));
            }
            catch (InvalidOperationException) { }
          }, stop.Token);
        if (!closed)
        {
          rxStatus.Text = "录音解析已完成";
          await IdentifyRecordedFskIdAsync(workingWave, stop.Token);
        }
      }
      catch (OperationCanceledException)
      {
        if (!closed) rxStatus.Text = "录音解析已停止";
      }
      catch (Exception ex)
      {
        if (!closed)
          MessageBox.Show(this, ex.Message, "无法解析 SSTV 录音",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
      finally
      {
        if (ReferenceEquals(replayStop, stop)) replayStop = null;
        stop.Dispose();
        if (!string.IsNullOrEmpty(normalizedTemporaryFile))
        {
          try { File.Delete(normalizedTemporaryFile); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }

    /// <summary>
    /// Decoded FSK station ID is a hint to the operator, not proof of
    /// a two-way QSO. QSO records still require explicit manual saving.
    /// </summary>
    private async Task IdentifyRecordedFskIdAsync(
      string path, CancellationToken token)
    {
      string? call = await Task.Run(
        () => SstvFskIdDecoder.DecodeWaveSuffix(path), token);
      if (closed || IsDisposed || string.IsNullOrWhiteSpace(call)) return;
      qsoCall.Text = call;
      rxStatus.Text = $"录音中的台站标识：{call}（请核对）";
    }

    private string QsoLogPath =>
      SstvQsoLogStore.DefaultLogPath(Utils.GetUserDataFolder());

    private void BuildQsoTab()
    {
      var page = new TabPage("通联日志");
      var bar = Toolbar();
      bar.Controls.Add(new Label
      {
        Text = "对方呼号", Width = 73, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(qsoCall);
      bar.Controls.Add(new Label
      {
        Text = "备注", Width = 40, Height = 30,
        TextAlign = ContentAlignment.MiddleCenter
      });
      bar.Controls.Add(qsoNotes);
      var actions = Toolbar();
      actions.Controls.Add(Btn("保存通联", SaveQsoRecord, 96));
      actions.Controls.Add(Btn("导出 ADIF", ExportQsoAdif, 106));
      page.Controls.Add(qsoList);
      page.Controls.Add(actions);
      page.Controls.Add(bar);
      tabs.TabPages.Add(page);
    }

    private void ReloadQsoLog()
    {
      qsoList.BeginUpdate();
      try
      {
        qsoList.Items.Clear();
        foreach (var record in SstvQsoLogStore.Read(QsoLogPath, 256))
          qsoList.Items.Add(new QsoEntry(record));
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        txStatus.Text = "通联日志暂时无法读取";
      }
      finally { qsoList.EndUpdate(); }
    }

    private void SaveQsoRecord()
    {
      try
      {
        string call = qsoCall.Text.Trim().ToUpperInvariant();
        // Logging and image review must also work before any radio is
        // connected. Prefer the actual received mode over a TX selection.
        RadioLink link = ctx.FrequencyControl?.RadioLink ?? new RadioLink();
        var selected = modeBox.SelectedItem as SstvTxModeProfile;
        var record = new SstvQsoRecord(
          DateTime.UtcNow, call, ctx.Settings.User.Call ?? "",
          lastReceivedMode ?? selected?.Name ?? "SSTV",
          link.IsTerrestrial ? "" : link.Sat?.name ?? "",
          link.IsTerrestrial ? 0 : link.CorrectedUplinkFrequency,
          link.CorrectedDownlinkFrequency,
          rxPicture.Tag as string ?? "",
          qsoNotes.Text.Trim());
        SstvQsoLogStore.Append(QsoLogPath, record);
        qsoCall.Clear();
        qsoNotes.Clear();
        ReloadQsoLog();
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法保存通联记录",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void ExportQsoAdif()
    {
      using var save = new SaveFileDialog
      {
        Filter = "ADIF 通联日志|*.adi",
        FileName = $"SSTV_{DateTime.Now:yyyyMMdd}.adi"
      };
      if (save.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        File.WriteAllText(save.FileName,
          SstvQsoLogStore.ExportAdif(SstvQsoLogStore.Read(QsoLogPath, 100000)),
          new System.Text.UTF8Encoding(false));
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法导出 ADIF",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private sealed record QsoEntry(SstvQsoRecord Record)
    {
      public override string ToString() =>
        $"{Record.Utc:yyyy-MM-dd HH:mm} UTC  {Record.Call}  {Record.SstvMode}  {Record.SatelliteName}";
    }

    private void PopulateTxOutputDevices()
    {
      txOutputBox.Items.Clear();
      try
      {
        foreach (AudioDeviceEntry d in Soundcard.ListDevices(DataFlow.Render))
          txOutputBox.Items.Add(new DeviceEntry(d.Id, d.Name));
        // Never choose Windows' default speaker as the TX audio route.
        // The operator must deliberately select the radio's own AF endpoint.
        txOutputBox.SelectedIndex = -1;
        txStatus.Text = txOutputBox.Items.Count > 0
          ? "请选择实际连接电台的发射声卡；不会自动使用系统默认扬声器。"
          : "未检测到发射声卡；仍可保存 SSTV WAV 音频";
      }
      catch (Exception ex)
      {
        Serilog.Log.Warning(ex, "Could not enumerate SSTV TX audio endpoints");
        txStatus.Text = "发射声卡不可用，请检查 Windows 音频设备";
      }
    }

    private void UpdateTxAvailability()
    {
      if (closed || IsDisposed) return;
      // A timed-out OFF command must remain visible to the operator and
      // prohibit a new TX until a subsequent, fresh hardware RX readback.
      bool releaseWasUnconfirmed = transmitSession.RequiresPttVerification;
      if (!transmitSession.VerifyPreviousPttRelease())
      {
        txSendButton.Enabled = false;
        txStatus.Text = SstvTxPolicy.PttReleaseUnconfirmedMessage;
        if (txApproval.Checked) txApproval.Checked = false;
        return;
      }
      if (releaseWasUnconfirmed)
      {
        txStatus.Text = "CAT 已确认电台返回接收状态，请重新核对发射频率与功率。";
        txApproval.Checked = false;
      }
      txSendButton.Enabled =
        txOperationStop == null &&
        !transmitSession.IsSending &&
        txApproval.Checked &&
        txPicture.Image is Bitmap &&
        txOutputBox.SelectedItem is DeviceEntry &&
        modeBox.SelectedItem is SstvTxModeProfile;
    }

    private void StopTransmit()
    {
      txOperationStop?.Cancel();
      transmitSession.RequestStop();
    }

    private async Task SendImageAsync()
    {
      if (txOperationStop != null || transmitSession.IsSending ||
          txOutputBox.SelectedItem is not DeviceEntry output ||
          txPicture.Image is not Bitmap image ||
          modeBox.SelectedItem is not SstvTxModeProfile spec)
      {
        if (!transmitSession.IsSending)
          MessageBox.Show(this, "请先选择图片、发送模式及发射声卡。", "SSTV",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (!txApproval.Checked)
      {
        MessageBox.Show(this, "请先核对电台上行频率、发射许可及发射功率。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (MessageBox.Show(this,
        $"确认通过所选声卡输出并启动电台 PTT 发送 {spec.Name}？" +
        "\n\n开始后可随时点击“停止发射”。",
        "确认 SSTV 发射", MessageBoxButtons.YesNo,
        MessageBoxIcon.Warning) != DialogResult.Yes) return;

      string temporaryWave = Path.Combine(Path.GetTempPath(),
        $"SkyRoof_SstvTx_{Guid.NewGuid():N}.wav");
      using var operation = new CancellationTokenSource();
      txOperationStop = operation;
      txSendButton.Enabled = false;
      txStopButton.Enabled = true;
      txApproval.Enabled = false;
      txOutputBox.Enabled = false;
      bool criticalPttAlertShown = false;
      try
      {
        // Refresh macros at the actual send instant, before taking a
        // picture snapshot. STOP can also abort the entire WAV preparation.
        RefreshTxPreview();
        using var snapshot = new Bitmap(txPicture.Image!);
        txStatus.Text = "正在准备 SSTV 图片...";
        SstvIdOptions? stationId = txCwId.Checked || txFskId.Checked
          ? new SstvIdOptions(ctx.Settings.User.Call ?? "",
            txCwId.Checked, txFskId.Checked)
          : null;
        await Task.Run(() =>
          SstvRgbWaveExporter.Export(snapshot, spec, temporaryWave,
            operation.Token, stationId),
          operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (closed) return;
        await transmitSession.SendWavAsync(
          temporaryWave, output.Id, txApproval.Checked,
          message => { if (!closed) txStatus.Text = message; },
          operation.Token);
      }
      catch (OperationCanceledException)
      {
        if (!closed)
          txStatus.Text = transmitSession.RequiresPttVerification
            ? SstvTxPolicy.PttReleaseUnconfirmedMessage : "发射已停止";
      }
      catch (Exception ex)
      {
        if (!closed)
        {
          bool pttUnconfirmed = transmitSession.RequiresPttVerification;
          txStatus.Text = pttUnconfirmed
            ? SstvTxPolicy.PttReleaseUnconfirmedMessage : "发射未完成";
          MessageBox.Show(this,
            pttUnconfirmed
              ? SstvTxPolicy.PttReleaseUnconfirmedMessage + "\n\n" + ex.Message
              : ex.Message,
            pttUnconfirmed ? "PTT 状态未确认" : "SSTV 发射已终止",
            MessageBoxButtons.OK,
            pttUnconfirmed ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
          criticalPttAlertShown = pttUnconfirmed;
        }
      }
      finally
      {
        if (ReferenceEquals(txOperationStop, operation)) txOperationStop = null;
        try { File.Delete(temporaryWave); } catch (IOException) { }
        if (!closed)
        {
          txStopButton.Enabled = false;
          txOutputBox.Enabled = true;
          txApproval.Enabled = true;
          txApproval.Checked = false;
          UpdateTxAvailability();
          // Cancellation is not a successful STOP acknowledgement. Preserve
          // this alarm even if the cancellation catch said "stopped".
          if (transmitSession.RequiresPttVerification)
          {
            txStatus.Text = SstvTxPolicy.PttReleaseUnconfirmedMessage;
            if (!criticalPttAlertShown)
              MessageBox.Show(this, SstvTxPolicy.PttReleaseUnconfirmedMessage,
                "PTT 状态未确认", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
          }
        }
      }
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
        if (rxDeviceBox.Items.Count > 0)
          rxDeviceBox.SelectedIndex = 0;
        else
          rxStatus.Text = "未检测到接收声卡；可使用“导入 WAV”解码录音";
      }
      catch (Exception ex)
      {
        Serilog.Log.Warning(ex, "Could not enumerate SSTV RX audio endpoints");
        rxStatus.Text = "接收声卡不可用，请检查 Windows 音频设备";
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

    private void ManualStartReceive()
    {
      if (rxModeBox.SelectedItem is not SstvTxModeProfile mode)
      {
        MessageBox.Show(this, "请先在接收模式中选定具体模式。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      if (!receiveSession.IsRunning)
      {
        MessageBox.Show(this, "请先启动接收声卡。", "SSTV",
          MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }
      receiveSession.ManualStart(mode);
      rxStatus.Text = $"已请求手动开始 {mode.Name}，等待扫描行";
    }

    private void OnLiveFskStationIdentified(string callsign)
    {
      if (closed || IsDisposed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (closed || IsDisposed) return;
          if (string.IsNullOrWhiteSpace(qsoCall.Text))
            qsoCall.Text = callsign;
          rxStatus.Text = $"收到 FSK 台站标识：{callsign}（需核对）";
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnSpectrumAvailable(float[] levels)
    {
      if (closed || IsDisposed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!closed && !IsDisposed)
            rxSpectrum.SetLevels(levels);
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnVisSynchronized(SstvVisAfcLock header)
    {
      if (closed || !IsHandleCreated) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (closed || IsDisposed) return;
          var native = SstvModes.FromVisByte(header.VisByte);
          string mode = native?.Name ??
            SstvRgbTxSpec.All.FirstOrDefault(x =>
              SstvModes.EvenParityByte(x.VisCode7) == header.VisByte)?.Name ??
            "SSTV";
          string status = autoVisCorrection.Checked ?
            $"识别 {mode} · 已自动校正 {receiveSession.AutoVisCorrectionHz:+0;-0;0} Hz" :
            $"识别 {mode} · 自动校频已关闭";
          rxStatus.Text = status;
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void OnRasterReceived(Bitmap picture,
      string mode, bool final, double correctionHz)
    {
      if (closed || IsDisposed || !IsHandleCreated)
      {
        picture.Dispose();
        return;
      }
      try
      {
        BeginInvoke((Action)(() =>
        {
          using (picture)
          {
            if (closed || IsDisposed) return;
            string? savedFile = null;
            if (final)
            {
              try
              {
                string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
                Directory.CreateDirectory(folder);
                string name =
                  $"SSTV_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{mode.Replace(' ', '_')}";
                savedFile = Path.Combine(folder, name + ".png");
                picture.Save(savedFile, ImageFormat.Png);
                File.WriteAllText(Path.Combine(folder, name + ".json"),
                  System.Text.Json.JsonSerializer.Serialize(new
                  {
                    Utc = DateTime.UtcNow,
                    Mode = mode,
                    Source = "实时声卡 AF/IF",
                    OffsetHz = correctionHz,
                    Picture = Path.GetFileName(savedFile)
                  }));
              }
              catch (Exception ex)
              {
                Serilog.Log.Warning(ex, "Could not archive extended SSTV image");
              }
            }
            ShowReceivedSstv(picture, mode, final, savedFile);
          }
        }));
      }
      catch (InvalidOperationException) { picture.Dispose(); }
    }

    private void OnAudioReceived(SstvImageEvent evt)
    {
      if (closed || !IsHandleCreated ||
          (!evt.Final && evt.ValidRows % 12 != 0)) return;
      Bitmap picture;
      try { picture = evt.Image.ToBitmap(); }
      catch { return; }
      string? savedFile = null;
      if (evt.Final && evt.ValidRows > 0)
      {
        try
        {
          string folder = Path.Combine(Utils.GetUserDataFolder(), "SstvImages");
          Directory.CreateDirectory(folder);
          string name = $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_Audio_{evt.Mode}_{evt.ImageId}";
          savedFile = Path.Combine(folder, name + ".png");
          picture.Save(savedFile, ImageFormat.Png);
          string json = System.Text.Json.JsonSerializer.Serialize(new
          {
            Utc = DateTime.UtcNow,
            Source = "WASAPI AF/IF",
            Mode = evt.Mode.ToString(),
            evt.ValidRows,
            evt.Image.Width,
            evt.Image.Height,
            evt.FromVis,
            Call = ctx.Settings.User.Call ?? ""
          });
          File.WriteAllText(Path.Combine(folder, name + ".json"), json);
        }
        catch (Exception e)
        {
          Serilog.Log.Warning(e, "SSTV receive image could not be saved");
        }
      }
      try
      {
        string? path = savedFile;
        BeginInvoke((Action)(() =>
        {
          using (picture)
          {
            if (!closed && !IsDisposed)
              ShowReceivedSstv(picture, evt.Mode.ToString(), evt.Final, path);
          }
        }));
      }
      catch (InvalidOperationException) { picture.Dispose(); }
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

    private static void ClearHistory(Stack<Bitmap> history)
    {
      while (history.TryPop(out Bitmap? entry)) entry.Dispose();
    }

    private static void PushHistory(Stack<Bitmap> stack, Bitmap image)
    {
      stack.Push(new Bitmap(image));
      if (stack.Count <= HistoryLimit) return;
      Bitmap[] newestFirst = stack.ToArray();
      stack.Clear();
      for (int i = HistoryLimit; i < newestFirst.Length; i++)
        newestFirst[i].Dispose();
      for (int i = HistoryLimit - 1; i >= 0; i--)
        stack.Push(newestFirst[i]);
    }

    private void SetTxImage(Bitmap image)
    {
      var old = txBase;
      if (old != null) PushHistory(undoImages, old);
      txBase = image;
      old?.Dispose();
      ClearHistory(redoImages);
      RefreshTxPreview();
      txStatus.Text = "图片已加载，可以编辑或保存 SSTV 音频";
      tabs.SelectedIndex = 1;
    }

    private string TemplatePath =>
      SstvTemplateStore.DefaultPath(Utils.GetUserDataFolder());

    private void ReloadUserTemplates()
    {
      try
      {
        for (int i = templateBox.Items.Count - 1; i >= 0; i--)
          if (templateBox.Items[i] is SstvUserTemplate)
            templateBox.Items.RemoveAt(i);
        foreach (SstvUserTemplate entry in SstvTemplateStore.Read(TemplatePath))
          templateBox.Items.Add(entry);
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        txStatus.Text = "图片模板无法读取";
      }
    }

    private void SaveUserTemplate()
    {
      using var dialog = new Form
      {
        Text = "保存图片模板", Size = new Size(380, 175),
        MinimumSize = new Size(380, 175), MaximumSize = new Size(380, 175),
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterParent,
        MaximizeBox = false, MinimizeBox = false
      };
      var label = new Label
      {
        Text = "模板名称", Left = 18, Top = 18, Width = 75, Height = 28
      };
      var input = new TextBox { Left = 98, Top = 15, Width = 255 };
      var ok = new Button
      {
        Text = "保存", Left = 172, Top = 65, Width = 88,
        DialogResult = DialogResult.OK
      };
      var cancel = new Button
      {
        Text = "取消", Left = 265, Top = 65, Width = 88,
        DialogResult = DialogResult.Cancel
      };
      dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
      dialog.AcceptButton = ok;
      dialog.CancelButton = cancel;
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try
      {
        string style = templateBox.SelectedItem is SstvUserTemplate source
          ? source.Style : templateBox.SelectedItem?.ToString() ?? "经典呼号";
        var record = new SstvUserTemplate(input.Text.Trim(), style,
          overlayText.Text, DateTime.UtcNow);
        SstvTemplateStore.Save(TemplatePath, record);
        ReloadUserTemplates();
        for (int i = 0; i < templateBox.Items.Count; i++)
          if (templateBox.Items[i] is SstvUserTemplate candidate &&
              candidate.Name == record.Name)
          {
            templateBox.SelectedIndex = i;
            break;
          }
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "无法保存图片模板",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void UndoImage()
    {
      if (txBase == null || !undoImages.TryPop(out Bitmap? prior)) return;
      PushHistory(redoImages, txBase);
      txBase.Dispose();
      txBase = prior;
      RefreshTxPreview();
    }

    private void RedoImage()
    {
      if (txBase == null || !redoImages.TryPop(out Bitmap? next)) return;
      PushHistory(undoImages, txBase);
      txBase.Dispose();
      txBase = next;
      RefreshTxPreview();
    }

    private void RotateImage()
    {
      if (txBase == null) return;
      var bitmap = new Bitmap(txBase);
      bitmap.RotateFlip(RotateFlipType.Rotate90FlipNone);
      SetTxImage(bitmap);
    }

    private void MirrorImage()
    {
      if (txBase == null) return;
      var bitmap = new Bitmap(txBase);
      bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
      SetTxImage(bitmap);
    }

    private void CropImage()
    {
      if (txBase == null) return;
      var mode = modeBox.SelectedItem as SstvTxModeProfile;
      double aspect = mode == null ? 4.0 / 3 : (double)mode.Width / mode.Height;
      int w = txBase.Width, h = txBase.Height;
      if ((double)w / h > aspect) w = Math.Max(1, (int)(h * aspect));
      else h = Math.Max(1, (int)(w / aspect));
      Rectangle crop = new((txBase.Width - w) / 2,
        (txBase.Height - h) / 2, w, h);
      var result = new Bitmap(w, h);
      using (var g = Graphics.FromImage(result))
        g.DrawImage(txBase, new Rectangle(0, 0, w, h), crop, GraphicsUnit.Pixel);
      SetTxImage(result);
    }

    private Bitmap? RenderTxPreview()
    {
      if (txBase == null) return null;
      SstvTxModeProfile spec = modeBox.SelectedItem as SstvTxModeProfile ?? SstvTxModeProfile.All[0];
      var canvas = new Bitmap(spec.Width, spec.Height, PixelFormat.Format32bppArgb);
      using var g = Graphics.FromImage(canvas);
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.Clear(Color.Black);
      g.DrawImage(txBase, new Rectangle(0, 0, canvas.Width, canvas.Height));
      string template = templateBox.SelectedItem is SstvUserTemplate saved
        ? saved.Style : templateBox.SelectedItem?.ToString() ?? "经典呼号";
      if (template == "QSL 卡片")
      {
        using var panel = new SolidBrush(Color.FromArgb(205, 16, 27, 43));
        g.FillRectangle(panel, 0, canvas.Height * .77f,
          canvas.Width, canvas.Height * .23f);
        using var captionFont = new Font(FontFamily.GenericSansSerif,
          Math.Max(9f, canvas.Width / 38f), FontStyle.Bold);
        g.DrawString("SSTV QSL", captionFont, Brushes.LightSkyBlue,
          canvas.Width * .035f, canvas.Height * .785f);
      }
      else if (template == "卫星横幅")
      {
        using var panel = new SolidBrush(Color.FromArgb(200, 9, 30, 57));
        g.FillRectangle(panel, 0, 0, canvas.Width, canvas.Height * .16f);
        using var captionFont = new Font(FontFamily.GenericSansSerif,
          Math.Max(9f, canvas.Width / 40f), FontStyle.Bold);
        g.DrawString("SATELLITE SSTV", captionFont, Brushes.White,
          canvas.Width * .035f, canvas.Height * .035f);
      }
      // Picture editing and offline WAV export work even when the radio
      // widget has not yet been initialized or no satellite is selected.
      RadioLink radio = ctx.FrequencyControl?.RadioLink ?? new RadioLink();
      var macros = new SstvMacroContext(
        ctx.Settings.User.Call ?? "", ctx.Settings.User.Square ?? "",
        qsoCall.Text.Trim().ToUpperInvariant(),
        radio.IsTerrestrial ? "" : radio.Sat?.name ?? "",
        (modeBox.SelectedItem as SstvTxModeProfile)?.Name ?? "SSTV",
        radio.IsTerrestrial ? 0 : radio.CorrectedUplinkFrequency,
        radio.CorrectedDownlinkFrequency,
        DateTime.UtcNow);
      string expanded = SstvTextMacros.Expand(overlayText.Text, macros);
      if (template != "纯图片" && !string.IsNullOrWhiteSpace(expanded))
      {
        float fontSize = Math.Max(12f, canvas.Width / 19f);
        using var font = new Font(FontFamily.GenericSansSerif, fontSize, FontStyle.Bold);
        using var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var foreground = new SolidBrush(Color.White);
        float x = Math.Max(8, canvas.Width * .035f);
        float y = Math.Max(6, canvas.Height - fontSize * 1.75f);
        g.DrawString(expanded, font, shadow, x + 2, y + 2);
        g.DrawString(expanded, font, foreground, x, y);
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
      UpdateTxAvailability();
    }

    private async Task ExportWaveAsync()
    {
      if (txPicture.Image is not Bitmap preview ||
          modeBox.SelectedItem is not SstvTxModeProfile spec)
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
        SstvIdOptions? stationId = txCwId.Checked || txFskId.Checked
          ? new SstvIdOptions(ctx.Settings.User.Call ?? "",
            txCwId.Checked, txFskId.Checked)
          : null;
        await Task.Run(() => SstvRgbWaveExporter.Export(
          snapshot, spec, save.FileName, default, stationId));
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
      // Never associate a new partial picture with the previous received
      // image in QSO logging. Keep the actual RX mode for the next log.
      rxPicture.Tag = final ? savedFile : null;
      if (final)
      {
        lastReceivedMode = mode;
        if (!string.IsNullOrWhiteSpace(savedFile))
          ReloadHistory();
      }
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
      StopTransmit();
      transmitSession.Dispose();
      replayStop?.Cancel();
      receiveSession.RasterImageAvailable -= OnRasterReceived;
      receiveSession.VisSynchronized -= OnVisSynchronized;
      receiveSession.SpectrumAvailable -= OnSpectrumAvailable;
      receiveSession.FskStationIdentified -= OnLiveFskStationIdentified;
      receiveSession.Dispose();
      if (ctx.SstvConsolePanel == this) ctx.SstvConsolePanel = null;
      var oldRx = rxPicture.Image;
      rxPicture.Image = null;
      oldRx?.Dispose();
      var oldTx = txPicture.Image;
      txPicture.Image = null;
      oldTx?.Dispose();
      txBase?.Dispose();
      ClearHistory(undoImages);
      ClearHistory(redoImages);
      base.OnFormClosed(e);
    }
  }
}
