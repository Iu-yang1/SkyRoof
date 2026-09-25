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
    private readonly Button SourceBtn = new();
    private readonly Button StartStopBtn = new();
    private readonly Button ClearBtn = new();
    private readonly Label StatusLabel = new();
    private readonly Label StatsLabel = new();
    private readonly Label TransportLabel = new();
    private readonly IcomLanSpectrumView SpectrumView = new();
    private readonly System.Windows.Forms.Timer UiTimer = new() { Interval = 500 };

    private IcomLanSpectrumCapture? Capture;
    private IcomLanSpectrumCapture? NativeLanAssistCapture;
    private long LastScopeFrames;
    private long LastScopeUpdates;
    private DateTime LastRateTime = DateTime.UtcNow;
    private DateTime LastScopeOutputRequestUtc = DateTime.MinValue;
    private bool LastScopeRequestRouted;
    private double ScopeFps;
    private double DisplayFps;
    private int SelectedScopeBand;
    private long LastRenderedScopeFrameTicks;
    private bool LastStatsUsedNativeLan;

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
        IcomLanSpectrumSettings settings =
          ctx.Settings.IcomLanSpectrum;

        if (settings.AutoStart &&
            settings.Source != IcomLanSpectrumSource.DirectLan)
        {
          StartCapture();
        }
        else if (settings.Source == IcomLanSpectrumSource.DirectLan)
        {
          StatusLabel.Text =
            "Direct LAN is experimental and never auto-starts. Close RS-BA1/other remote clients, then click Start manually.";
        }
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

      SourceBtn.AutoSize = true;
      SourceBtn.Margin = new Padding(0, 2, 8, 2);
      SourceBtn.Click += (_, _) => ToggleScopeSource();
      toolbar.Controls.Add(SourceBtn);

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

      TransportLabel.AutoSize = true;
      TransportLabel.ForeColor = SystemColors.GrayText;
      TransportLabel.Margin = new Padding(4, 7, 0, 0);
      toolbar.Controls.Add(TransportLabel);

      root.Controls.Add(toolbar, 0, 0);

      StatusLabel.Dock = DockStyle.Fill;
      StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatusLabel.ForeColor = SystemColors.GrayText;
      StatusLabel.Text =
        "Stopped. Select SkyCAT or RS-BA1 as the spectrum source, then start capture.";
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
      UpdateSourceButton();
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

    private bool UsingSkyCatScopeSource =>
      ctx.Settings.IcomLanSpectrum.Source == IcomLanSpectrumSource.SkyCat;

    private bool UsingDirectLanSource =>
      ctx.Settings.IcomLanSpectrum.Source == IcomLanSpectrumSource.DirectLan;

    private void UpdateSourceButton()
    {
      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;

      SourceBtn.Text = settings.Source switch
      {
        IcomLanSpectrumSource.DirectLan => "Source: Direct LAN",
        IcomLanSpectrumSource.RsBa1 => "Source: RS-BA1",
        _ => "Source: SkyCAT"
      };

      TransportLabel.Text = settings.Source switch
      {
        IcomLanSpectrumSource.DirectLan =>
          $"Experimental authenticated Icom LAN · manual start · control UDP/{settings.DirectLanControlPort}",
        IcomLanSpectrumSource.RsBa1 =>
          "Passive RS-BA1 LAN sniff · WinDivert RECV_ONLY",
        _ =>
          $"SkyCAT control · native LAN assist · TCP/{settings.SkyCatScopePort} fallback"
      };
    }

    private void ToggleScopeSource()
    {
      bool wasRunning = Capture != null;

      if (wasRunning)
        StopCapture();

      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;
      settings.Source = settings.Source switch
      {
        IcomLanSpectrumSource.SkyCat => IcomLanSpectrumSource.DirectLan,
        IcomLanSpectrumSource.DirectLan => IcomLanSpectrumSource.RsBa1,
        _ => IcomLanSpectrumSource.SkyCat
      };

      ctx.Settings.SaveToFile();
      UpdateSourceButton();

      LastScopeRequestRouted = false;
      LastScopeOutputRequestUtc = DateTime.MinValue;
      SpectrumView.Clear();

      if (wasRunning)
      {
        StartCapture();
        return;
      }

      StatusLabel.Text = settings.Source switch
      {
        IcomLanSpectrumSource.DirectLan =>
          "Direct LAN selected (experimental/manual). Close RS-BA1/other remote clients, configure credentials, then click Start.",
        IcomLanSpectrumSource.RsBa1 =>
          "RS-BA1 selected. Open/enable the RS-BA1 Spectrum Scope, then start passive LAN capture.",
        _ =>
          $"SkyCAT selected. Start capture to use scope TCP/{settings.SkyCatScopePort}."
      };
    }

    private void StartCapture()
    {
      if (Capture != null) return;

      SaveUiSettings();

      IcomLanSpectrumSettings settings = ctx.Settings.IcomLanSpectrum;
      SpectrumView.SetHistoryRows(settings.WaterfallRows);

      if (UsingDirectLanSource &&
          (string.IsNullOrWhiteSpace(settings.RadioAddress) ||
           string.IsNullOrWhiteSpace(settings.DirectLanUsername) ||
           string.IsNullOrEmpty(settings.DirectLanPassword)))
      {
        StatusLabel.Text =
          "Direct LAN requires Radio IPv4 address, Direct LAN username and Direct LAN password in Settings.";
        return;
      }

      var capture = new IcomLanSpectrumCapture(
        settings.RadioAddress,
        settings.SerialPort,
        useSkyCatStream: UsingSkyCatScopeSource,
        skyCatScopePort: settings.SkyCatScopePort,
        autoDiscoverCivPort: false,
        useDirectLan: UsingDirectLanSource,
        directLanControlPort: settings.DirectLanControlPort,
        directLanUsername: settings.DirectLanUsername,
        directLanPassword: settings.DirectLanPassword,
        directLanClientName: settings.DirectLanClientName);

      capture.ScopeFrameReceived += Capture_ScopeFrameReceived;
      capture.StatusChanged += Capture_StatusChanged;

      Capture = capture;

      // SkyCAT can receive serial-style multi-division 27 00 frames from the
      // Remote Utility path. Observe the RS-BA1 LAN C1 transport independently
      // and prefer current combined LAN sweeps when they are actually present;
      // otherwise keep the SkyCAT TCP stream as the fallback. Division count is a
      // wire-format distinction here, not an assumed frame-rate bottleneck.
      if (UsingSkyCatScopeSource)
      {
        var nativeLan = new IcomLanSpectrumCapture(
          settings.RadioAddress,
          settings.SerialPort,
          useSkyCatStream: false,
          skyCatScopePort: settings.SkyCatScopePort,
          autoDiscoverCivPort: true);

        nativeLan.ScopeFrameReceived += NativeLanAssist_ScopeFrameReceived;
        NativeLanAssistCapture = nativeLan;
      }
      LastScopeFrames = 0;
      LastScopeUpdates = 0;
      LastRateTime = DateTime.UtcNow;
      ScopeFps = 0;
      DisplayFps = 0;
      LastRenderedScopeFrameTicks = 0;
      LastStatsUsedNativeLan = false;

      SetCaptureInputsEnabled(false);
      StartStopBtn.Text = "Stop";
      StatusLabel.Text = settings.Source switch
      {
        IcomLanSpectrumSource.DirectLan =>
          $"Authenticating experimental Direct LAN session to {settings.RadioAddress}:{settings.DirectLanControlPort}...",
        IcomLanSpectrumSource.RsBa1 =>
          "Starting WinDivert passive RS-BA1 LAN capture...",
        _ =>
          $"Starting SkyCAT scope control with native LAN assist and TCP/{settings.SkyCatScopePort} fallback..."
      };

      NativeLanAssistCapture?.Start();
      capture.Start();

      if (UsingSkyCatScopeSource)
        RequestScopeOutputIfDue(force: true);
    }

    private void StopCapture()
    {
      IcomLanSpectrumCapture? capture = Capture;
      if (capture == null) return;

      IcomLanSpectrumCapture? nativeLan = NativeLanAssistCapture;
      Capture = null;
      NativeLanAssistCapture = null;

      if (nativeLan != null)
      {
        nativeLan.ScopeFrameReceived -= NativeLanAssist_ScopeFrameReceived;
        nativeLan.Dispose();
      }

      capture.ScopeFrameReceived -= Capture_ScopeFrameReceived;
      capture.StatusChanged -= Capture_StatusChanged;
      capture.Dispose();

      SetCaptureInputsEnabled(true);
      StartStopBtn.Text = "Start";
      StatusLabel.Text = "Spectrum capture stopped.";
    }

    private void SetCaptureInputsEnabled(bool enabled)
    {
      RadioAddressBox.Enabled = enabled;
      SerialPortBox.Enabled = enabled;
    }

    private void Capture_ScopeFrameReceived(IcomScopeFrame frame)
    {
      if (IsDisposed || !IsHandleCreated) return;

      // When a current combined LAN waveform is arriving, do not interleave a
      // second serial-style representation of the same scope into the display.
      DateTime? nativeLast = NativeLanAssistCapture?.LastScopeFrameUtc;
      if (nativeLast != null &&
          (DateTime.UtcNow - nativeLast.Value).TotalSeconds < 0.75)
        return;

      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!IsDisposed)
            RenderScopeFrame(frame);
        }));
      }
      catch (InvalidOperationException)
      {
        // The panel is closing.
      }
    }

    private void NativeLanAssist_ScopeFrameReceived(IcomScopeFrame frame)
    {
      if (IsDisposed || !IsHandleCreated) return;

      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!IsDisposed)
            RenderScopeFrame(frame);
        }));
      }
      catch (InvalidOperationException)
      {
        // The panel is closing.
      }
    }

    private void RenderScopeFrame(IcomScopeFrame frame)
    {
      long ticks = frame.TimestampUtc.Ticks;
      if (ticks <= LastRenderedScopeFrameTicks)
        return;

      IcomLanScopeBand selected =
        (IcomLanScopeBand)Volatile.Read(ref SelectedScopeBand);

      if (selected == IcomLanScopeBand.Main && frame.Scope != 0) return;
      if (selected == IcomLanScopeBand.Sub && frame.Scope != 1) return;

      SpectrumView.PushFrame(frame);
      LastRenderedScopeFrameTicks = ticks;
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

      DateTime now = DateTime.UtcNow;
      IcomLanSpectrumCapture? nativeLan = NativeLanAssistCapture;
      DateTime? nativeLast = nativeLan?.LastScopeFrameUtc;
      bool nativeLanActive =
        nativeLan != null &&
        nativeLast != null &&
        (now - nativeLast.Value).TotalSeconds < 1.0;

      IcomLanSpectrumCapture effectiveCapture =
        nativeLanActive ? nativeLan! : capture;
      long scopeFrames = effectiveCapture.ScopeFrameCount;
      long scopeUpdates = effectiveCapture.ScopeUpdateCount;

      // Event delivery is the normal high-rate path. Pull the newest frame as a
      // fallback if WinForms temporarily delays BeginInvoke during docking/layout.
      IcomScopeFrame? latestFrame = effectiveCapture.LatestScopeFrame;
      if (latestFrame != null &&
          latestFrame.TimestampUtc.Ticks > LastRenderedScopeFrameTicks)
        RenderScopeFrame(latestFrame);

      if (nativeLanActive != LastStatsUsedNativeLan)
      {
        LastStatsUsedNativeLan = nativeLanActive;
        LastScopeFrames = scopeFrames;
        LastScopeUpdates = scopeUpdates;
        LastRateTime = now;
        ScopeFps = 0;
        DisplayFps = 0;
      }

      double elapsed = (now - LastRateTime).TotalSeconds;

      if (elapsed >= 0.4)
      {
        ScopeFps = (scopeFrames - LastScopeFrames) / elapsed;
        DisplayFps = (scopeUpdates - LastScopeUpdates) / elapsed;
        LastScopeFrames = scopeFrames;
        LastScopeUpdates = scopeUpdates;
        LastRateTime = now;
      }

      string radio =
        capture.IsDirectLan
          ? $"Direct LAN {capture.DetectedRadioAddress ?? ctx.Settings.IcomLanSpectrum.RadioAddress}:" +
            $"{capture.DetectedCivPort?.ToString() ?? "negotiating"}"
          : nativeLanActive
            ? $"LAN {nativeLan!.DetectedRadioAddress ?? "auto"}:" +
              $"{nativeLan.DetectedCivPort?.ToString() ?? "auto"}"
            : capture.IsSkyCatStream
              ? "SkyCAT TCP fallback"
              : capture.DetectedRadioAddress ??
                (string.IsNullOrWhiteSpace(ctx.Settings.IcomLanSpectrum.RadioAddress)
                  ? "auto"
                  : ctx.Settings.IcomLanSpectrum.RadioAddress);

      StatsLabel.Text =
        $"Source {radio} · Frames {effectiveCapture.PacketCount:N0} · " +
        $"CI-V {effectiveCapture.CivFrameCount:N0} · Sweeps {scopeFrames:N0} · " +
        $"{ScopeFps:0.0}/s · Display {DisplayFps:0.0} fps · " +
        $"BadScope {effectiveCapture.InvalidScopeFrameCount:N0}" +
        (!effectiveCapture.IsSkyCatStream
          ? $" · Gaps {effectiveCapture.SequenceGapCount:N0} · Duplicates {effectiveCapture.DuplicateChunkCount:N0}"
          : "");

      DateTime? last = effectiveCapture.LastScopeFrameUtc;
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
          capture.IsDirectLan
            ? capture.PacketCount == 0
              ? "Direct LAN authenticated/connecting, but no native CI-V waveform has arrived yet."
              : "Direct LAN CI-V is active, but no complete native 27 00 sweep has been decoded yet."
            : capture.IsSkyCatStream
              ? capture.PacketCount == 0
                ? $"Connected/waiting for SkyCAT scope TCP/{ctx.Settings.IcomLanSpectrum.SkyCatScopePort}; " +
                  "CI-V 27 10 / 27 11 are being reasserted."
                : "SkyCAT scope stream is connected, but no complete CI-V 27 00 sweep has been assembled yet."
              : capture.PacketCount == 0
                ? $"Listening for IC-9700 UDP/{ctx.Settings.IcomLanSpectrum.SerialPort} traffic..."
                : "RS-BA1 LAN traffic detected, but no CI-V 27 00 waveform yet. " +
                  "Toggle the RS-BA1 Spectrum Scope CLOSED/OPEN and compare the passive observation.";
      }
      else if (capture.IsRunning && last != null)
      {
        StatusLabel.Text =
          capture.IsDirectLan
            ? $"Receiving native combined IC-9700 475-bin LAN waveform · {capture.TransportName}."
            : nativeLanActive
              ? $"Receiving high-rate native IC-9700 LAN 27 00 waveform · " +
                $"{nativeLan!.DetectedRadioAddress ?? "radio"}:" +
                $"{nativeLan.DetectedCivPort?.ToString() ?? "auto"} · SkyCAT controls scope."
              : $"Receiving IC-9700 CI-V 27 00 spectrum data · {capture.TransportName}.";
      }
    }

    private void RequestScopeOutputIfDue(bool force)
    {
      if (!UsingSkyCatScopeSource)
      {
        LastScopeRequestRouted = false;
        return;
      }

      DateTime now = DateTime.UtcNow;

      if (!force && (now - LastScopeOutputRequestUtc).TotalSeconds < 2.0)
        return;

      LastScopeOutputRequestUtc = now;

      LastScopeRequestRouted = ctx.CatControl.RequestIcomScopeOutput();
    }

    internal void ApplySettings()
    {
      bool restart = Capture != null;

      if (restart)
        StopCapture();

      LoadSettingsToUi();

      if (restart)
        StartCapture();
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
