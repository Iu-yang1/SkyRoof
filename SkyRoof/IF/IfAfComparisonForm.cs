using System;
using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using SkyRoof.IF;
using VE3NEA;

namespace SkyRoof
{
  /// <summary>Independent two-source hardware comparison recorder UI.
  /// The existing single-source Recorder remains unchanged.</summary>
  public sealed class IfAfComparisonForm : Form
  {
    private readonly Context ctx;
    private readonly IfAfComparisonRecorder recorder = new();
    private readonly ComboBox ifDevice = new()
    {
      Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox afDevice = new()
    {
      Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly ComboBox ifChannel = new()
    {
      Width = 115, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly TextBox destination = new()
    {
      Dock = DockStyle.Fill, ReadOnly = true
    };
    private readonly TextBox notes = new()
    {
      Dock = DockStyle.Fill, Multiline = true,
      ScrollBars = ScrollBars.Vertical
    };
    private readonly Button start = new() { Text = "开始双路录音", Width = 125 };
    private readonly Button stop = new() { Text = "停止并保存", Width = 110 };
    private readonly Button browse = new() { Text = "选择目录…", Width = 100 };
    private readonly Button open = new() { Text = "打开结果目录", Width = 120 };
    private readonly Label status = new()
    {
      Dock = DockStyle.Fill, AutoSize = false,
      TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    private string? lastFolder;
    private sealed record AudioEndpointOption(string Id, string Name)
    {
      public override string ToString() => Name;
    }

    public IfAfComparisonForm(Context ctx)
    {
      this.ctx = ctx;
      Text = "IC-9700 IF / 原机 AF 同步对照录音 — IF-AF-Test";
      StartPosition = FormStartPosition.CenterParent;
      MinimumSize = new Size(650, 420);
      ClientSize = new Size(760, 420);

      var table = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, Padding = new Padding(12),
        ColumnCount = 2, RowCount = 8
      };
      table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
      table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
      table.RowStyles.Add(new RowStyle(SizeType.Absolute, 63));
      table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

      void AddRow(string text, Control ctrl, int row)
      {
        table.Controls.Add(new Label
        {
          Text = text, Dock = DockStyle.Fill,
          TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        table.Controls.Add(ctrl, 1, row);
      }
      AddRow("USB 12 kHz IF 录音设备", ifDevice, 0);
      AddRow("LAN / RS-BA1 AF 播放设备", afDevice, 1);
      AddRow("USB IF 输入声道", ifChannel, 2);

      var locationRow = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1
      };
      locationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      locationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
      locationRow.Controls.Add(destination, 0, 0);
      locationRow.Controls.Add(browse, 1, 0);
      AddRow("保存到", locationRow, 3);
      AddRow("录音备注 / AGC / 带宽", notes, 4);

      var buttons = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false
      };
      buttons.Controls.AddRange(new Control[] { start, stop, open });
      table.Controls.Add(buttons, 1, 5);
      table.Controls.Add(new Label
      {
        Text = "两路原始音频将分别保存为 48 kHz mono float WAV；" +
          "另存每块 UTC 时间戳 CSV 和 capture.json。录音不会发送 CAT/PTT 命令。" +
          " LAN 为整条播放端点的混音，请为 RS-BA1 使用独立音频端点。",
        Dock = DockStyle.Fill, AutoSize = false,
        ForeColor = SystemColors.GrayText
      }, 0, 6);
      table.SetColumnSpan(table.GetControlFromPosition(0, 6)!, 2);
      table.Controls.Add(status, 0, 7);
      table.SetColumnSpan(status, 2);
      Controls.Add(table);

      ifChannel.Items.AddRange(new object[] { "Left", "Right", "Mix" });
      ifChannel.SelectedIndex = ctx.Settings.IfAf.CaptureChannel switch
      {
        AudioChannelChoice.Right => 1,
        AudioChannelChoice.Mix => 2,
        _ => 0
      };
      foreach (var entry in Soundcard.ListDevices(DataFlow.Capture))
        ifDevice.Items.Add(new AudioEndpointOption(entry.Id, entry.Name));
      foreach (var entry in Soundcard.ListDevices(DataFlow.Render))
        afDevice.Items.Add(new AudioEndpointOption(entry.Id, entry.Name));

      SelectById(ifDevice, ctx.Settings.IfAf.InputDeviceId ??
        ctx.Settings.Audio.RecorderCaptureDeviceId);
      SelectById(afDevice, ctx.Settings.Audio.RecorderLoopbackDeviceId ??
        ctx.Settings.Audio.RsBa1PlaybackDeviceId);
      destination.Text = Path.Combine(Utils.GetUserDataFolder(), "IfAfComparisons");
      status.Text = "准备就绪。请选择 IC-9700 USB IF 和 RS-BA1 LAN AF 对应的音频端点。";
      browse.Click += (_, _) =>
      {
        using var dlg = new FolderBrowserDialog
        {
          Description = "选择 IF/AF 双路对照录音的父目录",
          SelectedPath = Directory.Exists(destination.Text)
            ? destination.Text : Utils.GetUserDataFolder()
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
          destination.Text = dlg.SelectedPath;
      };
      start.Click += (_, _) => StartCapture();
      stop.Click += (_, _) => StopCapture();
      open.Click += (_, _) => OpenFolder();
      refresh.Tick += (_, _) => UpdateStatus();
      refresh.Start();
      UpdateStatus();
    }

    private static void SelectById(ComboBox box, string? id)
    {
      if (string.IsNullOrWhiteSpace(id)) return;
      for (int i = 0; i < box.Items.Count; i++)
        if (box.Items[i] is AudioEndpointOption entry && entry.Id == id)
        {
          box.SelectedIndex = i;
          return;
        }
    }

    private void StartCapture()
    {
      try
      {
        if (ifDevice.SelectedItem is not AudioEndpointOption usb ||
            afDevice.SelectedItem is not AudioEndpointOption lan)
          throw new InvalidOperationException("请先分别选择 USB IF 录音端点和 LAN AF 播放端点。");

        var choice = ifChannel.SelectedIndex switch
        {
          1 => AudioChannelChoice.Right,
          2 => AudioChannelChoice.Mix,
          _ => AudioChannelChoice.Left
        };
        var link = ctx.FrequencyControl.RadioLink;
        recorder.Start(usb.Id, lan.Id, choice, destination.Text, ctx.IfAf,
          link.TxCust != null ? link.DownlinkMode.ToString() : "Unknown",
          link.TxCust != null ? link.CorrectedDownlinkFrequency : 0,
          notes.Text);
        lastFolder = recorder.SessionFolder;
        UpdateStatus();
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "IF / AF 双路录音",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void StopCapture()
    {
      try
      {
        var summary = recorder.StopAsync().GetAwaiter().GetResult();
        if (summary == null) return;
        lastFolder = summary.SessionFolder;
        bool good = summary.IfSamples > 0 && summary.AfSamples > 0 &&
          summary.IfDroppedBlocks == 0 && summary.AfDroppedBlocks == 0 &&
          summary.IfError == null && summary.AfError == null;
        status.Text = good
          ? $"已保存 IF {summary.IfSamples}、AF {summary.AfSamples} samples；" +
            "可打开结果目录上传双 WAV + JSON + CSV。"
          : "录音已保存，但某个通道没有样本、发生丢块或写盘错误。请检查结果目录的 capture.json。";
        MessageBox.Show(this,
          $"录音目录：{summary.SessionFolder}\n" +
          $"IF: {summary.IfSamples} samples，丢块 {summary.IfDroppedBlocks}\n" +
          $"AF: {summary.AfSamples} samples，丢块 {summary.AfDroppedBlocks}\n" +
          (good ? "结果已保存。": "请检查端点和录音元数据。"),
          "IF / AF 双路录音", MessageBoxButtons.OK,
          good ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "双路录音保存错误",
          MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
      finally { UpdateStatus(); }
    }

    private void OpenFolder()
    {
      try
      {
        string path = lastFolder ?? destination.Text;
        if (!Directory.Exists(path))
          throw new DirectoryNotFoundException(path);
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        info.ArgumentList.Add(path);
        Process.Start(info);
      }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "打开录音目录",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void UpdateStatus()
    {
      bool running = recorder.IsRecording;
      start.Enabled = !running;
      stop.Enabled = running;
      ifDevice.Enabled = afDevice.Enabled = ifChannel.Enabled = !running;
      browse.Enabled = !running;
      if (running)
        status.Text = $"● 录音中  IF: {recorder.IfSamples / 48000.0:F1} s" +
          $"  AF: {recorder.AfSamples / 48000.0:F1} s" +
          $"  丢失音频块: {recorder.DroppedBlocks}" +
          "\n两路回调时间戳会随 WAV 一同保存，设备采样时钟不同属正常现象。";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      refresh.Stop();
      // Drain writing workers to finalize RIFF lengths even if the operator
      // closes the window without first clicking Stop.
      try { recorder.Dispose(); }
      catch (Exception ex)
      {
        MessageBox.Show(this, ex.Message, "录音关闭异常",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
      refresh.Dispose();
      base.OnFormClosing(e);
    }
  }
}
