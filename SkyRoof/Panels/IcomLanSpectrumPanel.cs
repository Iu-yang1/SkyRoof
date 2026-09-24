using System.ComponentModel;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public sealed class IcomLanSpectrumPanel : DockContent
  {
    private readonly Context ctx;

    private readonly TextBox RadioAddressBox = new();
    private readonly NumericUpDown SerialPortBox = new();
    private readonly ComboBox ScopeBandBox = new();
    private readonly Button StartStopBtn = new();
    private readonly Button ClearBtn = new();
    private readonly Label StatusLabel = new();
    private readonly Label StatsLabel = new();
    private readonly IcomLanSpectrumView SpectrumView = new();
    private readonly System.Windows.Forms.Timer UiTimer = new() { Interval = 500 };

    private IcomLanSpectrumCapture? Capture;
    private long LastScopeFrames;
    private DateTime LastRateTime = DateTime.UtcNow;
    private DateTime LastScopeOutputRequestUtc = DateTime.MinValue;
    private bool LastScopeRequestRouted;
    private double ScopeFps;
    private int SelectedScopeBand;

    public IcomLanSpectrumPanel(Context ctx)
    {
      this.ctx = ctx;

      Text = "Icom LAN Spectrum";
      Name = "IcomLanSpectrumPanel";
      ClientSize = new Size(940, 620);
      MinimumSize = new Size(560, 360);

      ctx.IcomLanSpectrumPanel = this;
      ctx.MainForm.IcomLanSpectrumMNU.Checked = true;

      BuildUi();
      LoadSettingsToUi();

      Shown += (_, _) =>
      {
        if (ctx.Settings.IcomLanSpectrum.AutoStart)
          StartCapture();
      };

      FormClosing += IcomLanSpectrumPanel_FormClosing;

      UiTimer.Tick += (_, _) => RefreshUiStatus();
      UiTimer.Start();
    }

    private void BuildUi()
    {
      var root = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 4,
        Padding = new Padding(8)
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

      var toolbar = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Margin = new Padding(0, 0, 0, 6)
      };

      toolbar.Controls.Add(new Label
      {
        AutoSize = true,
        Text = "Radio IP:",
        Margin = new Padding(0, 7, 4, 0)
      });

      RadioAddressBox.Width = 135;
      RadioAddressBox.PlaceholderText = "Auto";
      RadioAddressBox.Margin = new Padding(0, 3, 8, 3);
      RadioAddressBox.Leave += (_, _) => SaveUiSettings();
      toolbar.Controls.Add(RadioAddressBox);

      toolbar.Controls.Add(new Label
      {
        AutoSize = true,
        Text = "CI-V UDP:",
        Margin = new Padding(0, 7, 4, 0)
      });

      SerialPortBox.Minimum = 1;
      SerialPortBox.Maximum = 65535;
      SerialPortBox.Width = 78;
      SerialPortBox.Margin = new Padding(0, 3, 8, 3);
      SerialPortBox.ValueChanged += (_, _) =>
      {
        if (!SerialPortBox.Focused) return;
        SaveUiSettings();
      };
      toolbar.Controls.Add(SerialPortBox);

      toolbar.Controls.Add(new Label
      {
        AutoSize = true,
        Text = "Scope:",
        Margin = new Padding(0, 7, 4, 0)
      });

      ScopeBandBox.DropDownStyle = ComboBoxStyle.DropDownList;
      ScopeBandBox.Width = 90;
      ScopeBandBox.Items.AddRange(new object[] { "Auto", "MAIN", "SUB" });
      ScopeBandBox.Margin = new Padding(0, 3, 8, 3);
      ScopeBandBox.SelectedIndexChanged += (_, _) =>
      {
        Volatile.Write(ref SelectedScopeBand, Math.Clamp(ScopeBandBox.SelectedIndex, 0, 2));
        SaveUiSettings();
      };
      toolbar.Controls.Add(ScopeBandBox);

      StartStopBtn.Text = "Start";
      StartStopBtn.AutoSize = true;
      StartStopBtn.Margin = new Padding(0, 2, 6, 2);
      StartStopBtn.Click += (_, _) =>
      {
        if (Capture == null) StartCapture();
        else StopCapture();
      };
      toolbar.Controls.Add(StartStopBtn);

      ClearBtn.Text = "Clear";
      ClearBtn.AutoSize = true;
      ClearBtn.Margin = new Padding(0, 2, 8, 2);
      ClearBtn.Click += (_, _) => SpectrumView.Clear();
      toolbar.Controls.Add(ClearBtn);

      toolbar.Controls.Add(new Label
      {
        AutoSize = true,
        Text = "Passive WinDivert sniff · no packet injection",
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(4, 7, 0, 0)
      });

      root.Controls.Add(toolbar, 0, 0);

      StatusLabel.Dock = DockStyle.Fill;
      StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatusLabel.ForeColor = SystemColors.GrayText;
      StatusLabel.Text =
        "Stopped. Open the RS-BA1 Spectrum Scope, then start passive capture.";
      root.Controls.Add(StatusLabel, 0, 1);

      StatsLabel.Dock = DockStyle.Fill;
      StatsLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatsLabel.ForeColor = SystemColors.GrayText;
      StatsLabel.Text = "Packets 0 · CI-V 0 · Scope 0 · 0.0 fps";
      root.Controls.Add(StatsLabel, 0, 2);

      SpectrumView.Dock = DockStyle.Fill;
      SpectrumView.Margin = new Padding(0);
      root.Controls.Add(SpectrumView, 0, 3);

      Controls.Add(root);
    }

    private void LoadSettingsToUi()
    {
      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;

      RadioAddressBox.Text = settings.RadioAddress ?? string.Empty;
      SerialPortBox.Value = Math.Clamp(settings.SerialPort, 1, 65535);
      ScopeBandBox.SelectedIndex = Math.Clamp((int)settings.ScopeBand, 0, 2);
      Volatile.Write(ref SelectedScopeBand, ScopeBandBox.SelectedIndex);
      SpectrumView.SetHistoryRows(settings.WaterfallRows);
    }

    private void SaveUiSettings()
    {
      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;
      settings.RadioAddress = RadioAddressBox.Text.Trim();
      settings.SerialPort = (int)SerialPortBox.Value;
      settings.ScopeBand =
        (IcomLanScopeBand)Math.Clamp(ScopeBandBox.SelectedIndex, 0, 2);
      ctx.Settings.SaveToFile();
    }

    private void StartCapture()
    {
      if (Capture != null) return;

      SaveUiSettings();

      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;
      SpectrumView.SetHistoryRows(settings.WaterfallRows);

      var capture = new IcomLanSpectrumCapture(
        settings.RadioAddress,
        settings.SerialPort);

      capture.ScopeFrameReceived += Capture_ScopeFrameReceived;
      capture.StatusChanged += Capture_StatusChanged;

      Capture = capture;
      LastScopeFrames = 0;
      LastRateTime = DateTime.UtcNow;
      ScopeFps = 0;

      SetCaptureInputsEnabled(false);
      StartStopBtn.Text = "Stop";
      StatusLabel.Text = "Starting WinDivert passive capture...";

      capture.Start();
      RequestScopeOutputIfDue(force: true);
    }

    private void StopCapture()
    {
      IcomLanSpectrumCapture? capture = Capture;
      if (capture == null) return;

      Capture = null;

      capture.ScopeFrameReceived -= Capture_ScopeFrameReceived;
      capture.StatusChanged -= Capture_StatusChanged;
      capture.Dispose();

      SetCaptureInputsEnabled(true);
      StartStopBtn.Text = "Start";
      StatusLabel.Text = "Icom LAN capture stopped.";
    }

    private void SetCaptureInputsEnabled(bool enabled)
    {
      RadioAddressBox.Enabled = enabled;
      SerialPortBox.Enabled = enabled;
    }

    private void Capture_ScopeFrameReceived(IcomScopeFrame frame)
    {
      IcomLanScopeBand selected =
        (IcomLanScopeBand)Volatile.Read(ref SelectedScopeBand);

      if (selected == IcomLanScopeBand.Main && frame.Scope != 0) return;
      if (selected == IcomLanScopeBand.Sub && frame.Scope != 1) return;

      if (IsDisposed || !IsHandleCreated) return;

      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!IsDisposed)
            SpectrumView.PushFrame(frame);
        }));
      }
      catch (InvalidOperationException)
      {
        // The panel is closing.
      }
    }

    private void Capture_StatusChanged(string message)
    {
      if (IsDisposed || !IsHandleCreated) return;

      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!IsDisposed)
            StatusLabel.Text = message;
        }));
      }
      catch (InvalidOperationException)
      {
        // The panel is closing.
      }
    }

    private void RefreshUiStatus()
    {
      IcomLanSpectrumCapture? capture = Capture;
      if (capture == null) return;

      long scopeFrames = capture.ScopeFrameCount;
      DateTime now = DateTime.UtcNow;
      double elapsed = (now - LastRateTime).TotalSeconds;

      if (elapsed >= 0.4)
      {
        ScopeFps = (scopeFrames - LastScopeFrames) / elapsed;
        LastScopeFrames = scopeFrames;
        LastRateTime = now;
      }

      string radio =
        capture.DetectedRadioAddress ??
        (string.IsNullOrWhiteSpace(ctx.Settings.IcomLanSpectrum.RadioAddress)
          ? "auto"
          : ctx.Settings.IcomLanSpectrum.RadioAddress);

      StatsLabel.Text =
        $"Radio {radio} · Packets {capture.PacketCount:N0} · " +
        $"Serial {capture.SerialChunkCount:N0} · CI-V {capture.CivFrameCount:N0} · " +
        $"Scope {scopeFrames:N0} · {ScopeFps:0.0} fps · " +
        $"BadScope {capture.InvalidScopeFrameCount:N0} · " +
        $"ChunkLenΔ {capture.LanChunkLengthMismatchCount:N0} · " +
        $"Gaps {capture.SequenceGapCount:N0} · Duplicates {capture.DuplicateChunkCount:N0}";

      DateTime? last = capture.LastScopeFrameUtc;
      bool scopeStale =
        last == null || (now - last.Value).TotalSeconds > 1.5;

      if (capture.IsRunning && scopeStale)
        RequestScopeOutputIfDue(force: false);

      if (capture.LastError != null)
      {
        StatusLabel.Text = capture.LastError;
      }
      else if (capture.IsRunning &&
               (last == null || (now - last.Value).TotalSeconds > 3))
      {
        StatusLabel.Text =
          capture.PacketCount == 0
            ? "Listening for IC-9700 UDP/50002 traffic..."
            : LastScopeRequestRouted
              ? "Icom LAN traffic detected, but no CI-V 27 00 waveform yet. " +
                "Scope output reasserted through SkyCAT; waiting for waveform data..."
              : "Icom LAN traffic detected, but no CI-V 27 00 waveform yet. " +
                "No active SkyCAT CAT engine is available to enable scope output.";
      }
      else if (capture.IsRunning && last != null)
      {
        StatusLabel.Text =
          "Receiving native IC-9700 CI-V 27 00 spectrum data · " +
          "WinDivert SNIFF/RECV_ONLY.";
      }
    }

    private void RequestScopeOutputIfDue(bool force)
    {
      DateTime now = DateTime.UtcNow;

      if (!force && (now - LastScopeOutputRequestUtc).TotalSeconds < 2.0)
        return;

      LastScopeOutputRequestUtc = now;

      LastScopeRequestRouted = ctx.CatControl.RequestIcomScopeOutput();
    }

    private void IcomLanSpectrumPanel_FormClosing(object? sender, FormClosingEventArgs e)
    {
      UiTimer.Stop();
      StopCapture();

      ctx.IcomLanSpectrumPanel = null;
      ctx.MainForm.IcomLanSpectrumMNU.Checked = false;
    }
  }
}
