using SkyRoof.CW;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// View over the shared CW receive worker and fail-closed SkyCAT
  /// Command-17 transmitter. An explicit Send or Shift+F1-F8 prepares the
  /// interlock on demand; received text is never automatically transmitted.
  /// Closing the panel stops/disarms any active keyer lease.
  /// </summary>
  public sealed class CwConsolePanel : DockContent
  {
    private readonly Context ctx;
    private readonly object stateSync = new();

    private readonly Button RxToggleBtn = new();
    private readonly ComboBox SourceBox = new();
    private readonly ComboBox SpectrumCleanupBox = new();
    private readonly Label SpectrumStatusLabel = new();
    private readonly Button SettingsBtn = new();
    private readonly Button InstallModelBtn = new();

    private readonly Label InputStatusLabel = new();
    private readonly Label ModelStatusLabel = new();
    private readonly Label WorkerStatusLabel = new();

    private readonly CwAudioWaterfallAnalyzer WaterfallAnalyzer;
    private readonly CwAudioWaterfallAnalyzer SpectrumAnalyzer;
    private readonly CwAudioWaterfallView WaterfallView =
      new(spectrumBins: 512);
    private long waterfallGeneration = -1;
    private long lastWaterfallSampleIndex = -1;
    private int spectrumFrameDivider;

    private readonly CwPileupLaneList PileupList = new();
    private readonly CwSelectedLaneView SelectedRxView = new();
    private readonly SplitContainer WorkSplit = new();
    private readonly VScrollBar AfPanBar = new();
    private readonly NumericUpDown AfZoomBox = new();
    private readonly Label AfWindowLabel = new();

    private readonly TextBox TxTextBox = new();
    private readonly Button SendTxBtn = new();
    private readonly Button StopTxBtn = new();
    private readonly Label TxStatusLabel = new();
    private readonly NumericUpDown KeySpeedBox = new();
    private readonly Button SetKeySpeedBtn = new();
    private readonly Button[] MacroButtons =
      Enumerable.Range(
        0,
        CwMacroBank.Count)
      .Select(_ => new Button())
      .ToArray();
    private readonly ToolTip MacroToolTip =
      new();
    private bool sendRequestInProgress;
    private CancellationTokenSource? sendRequestStop;
    private bool statusPollInProgress;
    private DateTime nextStatusPollUtc = DateTime.MinValue;

    private readonly System.Windows.Forms.Timer UiTimer =
      new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer WaterfallTimer =
      new() { Interval = 50 };

    private IReadOnlyList<CwSignalTrack> latestTracks =
      Array.Empty<CwSignalTrack>();
    private readonly Dictionary<
      CwConsoleLaneIdentity,
      CwTranscriptSnapshot> transcripts =
        new();
    private readonly CwConsoleLaneSlotMap laneSlots =
      new(maxSlots: 8);

    private long displayGeneration = -1;
    private CwConsoleLaneIdentity? selectedIdentity;
    private bool updatingSourceUi;
    private HamNoiseAudioDenoiser? displayDenoiser;
    private CwDenoiseMode displayDenoiserMode =
      CwDenoiseMode.Bypass;
    private bool modelInstalled;
    private CancellationTokenSource? modelInstallStop;

    public CwConsolePanel(Context ctx)
    {
      this.ctx =
        ctx ??
        throw new ArgumentNullException(nameof(ctx));

      CwFrameRidgeScannerOptions? scanner =
        ctx.CwAudio?
          .Ingress
          .FrontEnd
          .FrameScanner
          .Options;

      int displayRate =
        scanner?.SampleRate ??
        SdrConst.AUDIO_SAMPLING_RATE;
      double displayMinHz =
        scanner?.MinFrequencyHz ?? 100;
      double displayMaxHz =
        scanner?.MaxFrequencyHz ?? 2000;

      // CW Skimmer-style split display: the spectrum gets a long FFT for
      // frequency resolution while the waterfall gets a short FFT and fast
      // line rate so dots/dashes remain visually recognizable.
      WaterfallAnalyzer =
        new CwAudioWaterfallAnalyzer(
          sampleRate: displayRate,
          fftSize: 2048,
          minFrequencyHz: displayMinHz,
          maxFrequencyHz: displayMaxHz,
          outputBins: 512);
      SpectrumAnalyzer =
        new CwAudioWaterfallAnalyzer(
          sampleRate: displayRate,
          fftSize: 8192,
          minFrequencyHz: displayMinHz,
          maxFrequencyHz: displayMaxHz,
          outputBins: 512);

      Text = "CW Console [TX disabled]";
      Name = "CwConsolePanel";
      // Keep the Console usable as a compact floating tool window. The old
      // 1040x900 / 720x720 geometry was unnecessarily tall on 1080p displays.
      ClientSize = new Size(1010, 640);
      MinimumSize = new Size(720, 560);
      KeyPreview = true;

      BuildUi();

      ctx.CwConsolePanel = this;
      ctx.MainForm.CwConsoleMNU.Checked = true;

      modelInstalled =
        DeepCwModelManager.IsInstalled();

      CwReceiveWorker? worker =
        ctx.CwReceiveWorker;
      if (worker != null)
      {
        worker.TracksUpdated +=
          Worker_TracksUpdated;
        worker.DecodeUpdated +=
          Worker_DecodeUpdated;
        worker.StatusChanged +=
          Worker_StatusChanged;

        lock (stateSync)
        {
          latestTracks =
            worker.LatestTracks.ToArray();
          CwContinuousDecodeBatch? decode =
            worker.LatestDecode;
          if (decode != null)
          {
            foreach (CwTranscriptSnapshot transcript
              in decode.Transcripts)
              transcripts[
                CwConsolePresentation.Identity(
                  transcript)] = transcript;
          }

          displayGeneration =
            worker.GetStatus()
              .TimelineGeneration;
        }
      }

      LoadSettingsIntoControls();
      RefreshUi();

      UiTimer.Tick +=
        UiTimer_Tick;
      UiTimer.Start();

      WaterfallTimer.Tick +=
        WaterfallTimer_Tick;
      WaterfallTimer.Start();

      FormClosing +=
        CwConsolePanel_FormClosing;
    }

    private void BuildUi()
    {
      var root =
        CreateSkimmerRootLayout();

      var toolbar =
        new FlowLayoutPanel
        {
          Dock = DockStyle.Fill,
          AutoSize = false,
          AutoScroll = true,
          Height = 36,
          WrapContents = false,
          FlowDirection =
            FlowDirection.LeftToRight,
          Margin = new Padding(
            0, 0, 0, 2)
        };

      RxToggleBtn.AutoSize = true;
      RxToggleBtn.Click +=
        RxToggleBtn_Click;
      toolbar.Controls.Add(
        RxToggleBtn);

      SourceBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SourceBox.Width = 205;
      SourceBox.DataSource =
        Enum.GetValues<
          CwReceiveAudioSource>();
      SourceBox.FormattingEnabled = true;
      SourceBox.Format +=
        (_, e) =>
        {
          if (e.ListItem is CwReceiveAudioSource source)
            e.Value =
              CwAudioSourceController
                .SourceDisplayName(source);
        };
      var sourceTip = new ToolTip();
      sourceTip.SetToolTip(
        SourceBox,
        "Choose the signal path here; choose the concrete Windows endpoint in Settings > CW Console.\r\n\r\n" +
        "IC-9700 USB AF input = Windows recording endpoint such as Microphone (USB Audio CODEC). The radio's USB AF/IF Output must be AF, not IF.\r\n" +
        "RS-BA1 playback loopback = the Windows playback/render endpoint that RS-BA1 is actually playing into.\r\n" +
        "SkyRoof SDR audio = the existing internal 48 kHz SDR/Slicer AF stream.");
      SourceBox.SelectedIndexChanged +=
        SourceBox_SelectedIndexChanged;
      toolbar.Controls.Add(
        SourceBox);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "Spectrum",
          Margin = new Padding(10, 7, 2, 0)
        });

      SpectrumCleanupBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SpectrumCleanupBox.Width = 150;
      SpectrumCleanupBox.DataSource =
        Enum.GetValues<CwDenoiseMode>();
      SpectrumCleanupBox.FormattingEnabled = true;
      SpectrumCleanupBox.Format +=
        (_, e) =>
        {
          if (e.ListItem is not CwDenoiseMode mode)
            return;
          e.Value = mode switch
          {
            CwDenoiseMode.Bypass => "Raw",
            CwDenoiseMode.HamNoiseClassic => "HamNoise Classic",
            CwDenoiseMode.HamNoiseV2 => "HamNoise CW V2",
            _ => mode.ToString()
          };
        };
      SpectrumCleanupBox.SelectedIndexChanged +=
        SpectrumCleanupBox_SelectedIndexChanged;
      toolbar.Controls.Add(
        SpectrumCleanupBox);

      SettingsBtn.Text = "CW Settings…";
      SettingsBtn.AutoSize = true;
      SettingsBtn.Click +=
        (_, _) =>
        {
          using var dialog =
            new SettingsDialog(ctx);
          dialog.ShowDialog(this);

          if (!ctx.Settings.CwConsole.TransmitEnabled &&
              ctx.CwTransmit?.State.Armed == true)
          {
            try
            {
              ctx.CwTransmit
                .DisarmAsync()
                .GetAwaiter()
                .GetResult();
            }
            catch
            {
              // Controller connection teardown still triggers SkyCAT's
              // disconnect fail-safe; the state retains the error for UI.
            }
          }

          LoadSettingsIntoControls();
        };
      toolbar.Controls.Add(
        SettingsBtn);

      InstallModelBtn.Text =
        "Install DeepCW Model";
      InstallModelBtn.AutoSize = true;
      InstallModelBtn.Click +=
        InstallModelBtn_Click;
      toolbar.Controls.Add(
        InstallModelBtn);

      root.Controls.Add(
        toolbar,
        0,
        0);

      var statusPanel =
        new FlowLayoutPanel
        {
          Dock = DockStyle.Fill,
          AutoSize = false,
          AutoScroll = true,
          Height = 27,
          WrapContents = false,
          FlowDirection =
            FlowDirection.LeftToRight,
          Margin = new Padding(
            0, 0, 0, 2)
        };

      InputStatusLabel.AutoSize = true;
      InputStatusLabel.Margin =
        new Padding(
          0, 4, 18, 4);
      ModelStatusLabel.AutoSize = true;
      ModelStatusLabel.Margin =
        new Padding(
          0, 4, 18, 4);
      WorkerStatusLabel.AutoSize = true;
      WorkerStatusLabel.Margin =
        new Padding(
          0, 4, 0, 4);

      statusPanel.Controls.Add(
        InputStatusLabel);
      statusPanel.Controls.Add(
        ModelStatusLabel);
      SpectrumStatusLabel.AutoSize = true;
      SpectrumStatusLabel.Margin =
        new Padding(
          0, 4, 18, 4);
      statusPanel.Controls.Add(
        SpectrumStatusLabel);
      statusPanel.Controls.Add(
        WorkerStatusLabel);

      root.Controls.Add(
        statusPanel,
        0,
        1);

      ConfigureSkimmerWorkspace();
      root.Controls.Add(WorkSplit, 0, 2);
      root.Controls.Add(SelectedRxView, 0, 3);
      root.Controls.Add(BuildTransmitPanel(), 0, 4);

      Controls.Add(root);
    }

    internal static TableLayoutPanel CreateSkimmerRootLayout()
    {
      var root = new TableLayoutPanel
      {
        ColumnCount = 1,
        RowCount = 5,
        Dock = DockStyle.Fill,
        AutoScroll = false,
        Padding = new Padding(6)
      };
      root.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      return root;
    }

    private void ConfigureSkimmerWorkspace()
    {
      WorkSplit.Dock = DockStyle.Fill;
      WorkSplit.Orientation = Orientation.Vertical;
      WorkSplit.SplitterWidth = 7;
      WorkSplit.BorderStyle = BorderStyle.FixedSingle;
      // Docked/narrow consoles may be smaller than the combined preferred
      // pane widths, so don't set hard minimums that make WinForms throw.
      WorkSplit.Panel1MinSize = 0;
      WorkSplit.Panel2MinSize = 0;

      var left = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        RowCount = 2,
        ColumnCount = 2,
        Padding = new Padding(0)
      };
      left.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      left.ColumnStyles.Add(
        new ColumnStyle(SizeType.Absolute, 19));
      left.RowStyles.Add(
        new RowStyle(SizeType.Percent, 100));
      left.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 30));

      WaterfallView.Dock = DockStyle.Fill;
      WaterfallView.Margin = new Padding(0);
      WaterfallView.LaneClicked +=
        WaterfallView_LaneClicked;
      WaterfallView.ViewportChanged +=
        WaterfallView_ViewportChanged;
      left.Controls.Add(WaterfallView, 0, 0);

      // AF is vertical now: scroll frequency up/down independently of
      // the horizontal time history (newest frames arrive on the right).
      AfPanBar.Dock = DockStyle.Fill;
      AfPanBar.Minimum = 0;
      AfPanBar.LargeChange = 100;
      AfPanBar.SmallChange = 10;
      AfPanBar.Maximum = 1099;
      AfPanBar.Value = 1000;
      AfPanBar.Enabled = false;
      AfPanBar.ValueChanged += (_, _) =>
        WaterfallView.SetViewport(
          (1000 - AfPanBar.Value) / 1000.0,
          (double)AfZoomBox.Value);
      left.Controls.Add(AfPanBar, 1, 0);

      var controls = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        AutoScroll = true,
        Margin = new Padding(0)
      };
      AfWindowLabel.Text = "AF ↑  ·  Time ←";
      AfWindowLabel.AutoSize = true;
      AfWindowLabel.Margin = new Padding(4, 7, 14, 0);
      controls.Controls.Add(AfWindowLabel);
      controls.Controls.Add(
        new Label
        {
          Text = "Zoom",
          AutoSize = true,
          Margin = new Padding(0, 7, 3, 0)
        });
      AfZoomBox.Minimum = 1;
      AfZoomBox.Maximum = 8;
      AfZoomBox.Increment = 1;
      AfZoomBox.Value = 1;
      AfZoomBox.Width = 45;
      AfZoomBox.ValueChanged += (_, _) =>
        WaterfallView.SetViewport(
          (1000 - AfPanBar.Value) / 1000.0,
          (double)AfZoomBox.Value);
      controls.Controls.Add(AfZoomBox);
      left.Controls.Add(controls, 0, 1);

      WorkSplit.Panel1.Controls.Add(left);
      PileupList.LaneSelected +=
        PileupList_LaneSelected;
      WorkSplit.Panel2.Controls.Add(PileupList);

      Shown += (_, _) =>
      {
        if (WorkSplit.ClientSize.Width > 0)
          WorkSplit.SplitterDistance = Math.Clamp(
            (int)(WorkSplit.ClientSize.Width * 0.68),
            1,
            Math.Max(1, WorkSplit.ClientSize.Width - 8));
      };
    }

    private void WaterfallView_ViewportChanged(
      object? sender, EventArgs e)
    {
      int value = Math.Clamp(
        (int)Math.Round(
          1000 - WaterfallView.ViewportStartFraction * 1000),
        0, 1000);
      if (AfPanBar.Value != value)
        AfPanBar.Value = value;

      AfPanBar.Enabled =
        WaterfallView.ViewportZoom > 1.0;
      decimal zoom = Math.Clamp(
        (decimal)WaterfallView.ViewportZoom,
        AfZoomBox.Minimum,
        AfZoomBox.Maximum);
      if (AfZoomBox.Value != zoom)
        AfZoomBox.Value = zoom;
    }

    private Control BuildTransmitPanel()
    {
      var group = new GroupBox
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(0, 145),
        Text = "CW TX — IC-9700 / SkyCAT",
        Margin = new Padding(0, 2, 0, 0),
        Padding = new Padding(5)
      };
      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        RowCount = 4,
        Margin = new Padding(0)
      };
      layout.ColumnStyles.Add(
        new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 22));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 28));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 35));
      layout.RowStyles.Add(
        new RowStyle(SizeType.Absolute, 32));

      TxStatusLabel.Text = "TX: disarmed";
      TxStatusLabel.Dock = DockStyle.Fill;
      TxStatusLabel.AutoEllipsis = true;
      layout.Controls.Add(TxStatusLabel, 0, 0);

      TxTextBox.Dock = DockStyle.Fill;
      TxTextBox.Multiline = false;
      TxTextBox.MaxLength = CwMessageTiming.MaxCharacters;
      TxTextBox.Font = new Font(
        FontFamily.GenericMonospace, 10.5f);
      TxTextBox.TextChanged +=
        (_, _) => RefreshTransmitUi();
      layout.Controls.Add(TxTextBox, 0, 1);

      var actions = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        WrapContents = false,
        Margin = new Padding(0, 2, 0, 0)
      };

      SendTxBtn.Text = "Send";
      SendTxBtn.AutoSize = true;
      SendTxBtn.Click += SendTxBtn_Click;
      actions.Controls.Add(SendTxBtn);

      StopTxBtn.Text = "STOP";
      StopTxBtn.AutoSize = true;
      StopTxBtn.BackColor = Color.Firebrick;
      StopTxBtn.ForeColor = Color.White;
      StopTxBtn.UseVisualStyleBackColor = false;
      StopTxBtn.Click += StopTxBtn_Click;
      actions.Controls.Add(StopTxBtn);

      actions.Controls.Add(new Label
      {
        Text = "WPM",
        AutoSize = true,
        Margin = new Padding(10, 8, 2, 0)
      });
      KeySpeedBox.Minimum =
        (decimal)CwMessageTiming.MinimumWpm;
      KeySpeedBox.Maximum =
        (decimal)CwMessageTiming.MaximumWpm;
      KeySpeedBox.DecimalPlaces = 1;
      KeySpeedBox.Increment = 0.5m;
      KeySpeedBox.Value = 20m;
      KeySpeedBox.Width = 64;
      actions.Controls.Add(KeySpeedBox);
      SetKeySpeedBtn.Text = "Set WPM";
      SetKeySpeedBtn.AutoSize = true;
      SetKeySpeedBtn.Click += SetKeySpeedBtn_Click;
      actions.Controls.Add(SetKeySpeedBtn);
      layout.Controls.Add(actions, 0, 2);

      var macros = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        WrapContents = false,
        Margin = new Padding(0)
      };
      for (int i = 0; i < MacroButtons.Length; i++)
      {
        Button button = MacroButtons[i];
        int index = i;
        button.AutoSize = false;
        button.Size = new Size(62, 25);
        button.Tag = index;
        button.Click += (_, _) =>
          LoadMacroIntoComposer(index);
        button.MouseUp += (_, e) =>
        {
          if (e.Button == MouseButtons.Right)
            EditMacro(index);
        };
        macros.Controls.Add(button);
      }
      layout.Controls.Add(macros, 0, 3);
      group.Controls.Add(layout);
      return group;
    }

    private void Worker_TracksUpdated(
      object? sender,
      CwTracksUpdatedEventArgs e)
    {
      lock (stateSync)
      {
        EnsureGenerationLocked(
          e.TimelineGeneration);
        latestTracks =
          e.Tracks.ToArray();
      }
    }

    private void Worker_DecodeUpdated(
      object? sender,
      CwDecodeUpdatedEventArgs e)
    {
      lock (stateSync)
      {
        EnsureGenerationLocked(
          e.TimelineGeneration);

        foreach (CwTranscriptSnapshot transcript
          in e.Batch.Transcripts)
        {
          transcripts[
            CwConsolePresentation.Identity(
              transcript)] = transcript;
        }
      }
    }

    private void Worker_StatusChanged(
      object? sender,
      EventArgs e)
    {
      // Status is intentionally polled on the UI timer. Do not BeginInvoke
      // every 120 ms just because the tracker advanced.
    }

    private void EnsureGenerationLocked(
      long generation)
    {
      if (generation ==
          displayGeneration)
        return;

      displayGeneration =
        generation;
      latestTracks =
        Array.Empty<CwSignalTrack>();
      transcripts.Clear();
      laneSlots.Reset();
      selectedIdentity = null;
    }

    private void UiTimer_Tick(
      object? sender,
      EventArgs e)
    {
      RefreshUi();
      PollTransmitStatusIfDue();
    }

    private void WaterfallTimer_Tick(
      object? sender,
      EventArgs e) =>
      RefreshWaterfall();

    private void RefreshUi()
    {
      RefreshSourceStatus();
      RefreshWorkerStatus();
      RefreshPileupCards();
      RefreshTransmitUi();
    }

    private void RefreshSourceStatus()
    {
      CwAudioInputStatus? status =
        ctx.CwAudio?.GetStatus();

      CwConsoleSettings settings =
        ctx.Settings.CwConsole;

      updatingSourceUi = true;
      try
      {
        RxToggleBtn.Text =
          settings.ReceiveEnabled
            ? "Stop RX"
            : "Start RX";
        RxToggleBtn.UseVisualStyleBackColor =
          !settings.ReceiveEnabled;
        if (settings.ReceiveEnabled)
        {
          RxToggleBtn.BackColor =
            Theme.QsoFieldEdited;
          RxToggleBtn.ForeColor =
            Color.White;
        }
        else
        {
          RxToggleBtn.BackColor =
            SystemColors.Control;
          RxToggleBtn.ForeColor =
            SystemColors.ControlText;
        }

        SourceBox.SelectedItem =
          settings.AudioSource;
      }
      finally
      {
        updatingSourceUi = false;
      }

      if (status == null)
      {
        InputStatusLabel.Text =
          "Input: unavailable";
        return;
      }

      string state =
        !status.Value.Enabled
          ? "disabled"
          : status.Value.Running
            ? "running"
            : "waiting";

      string role =
        CwAudioSourceController.SourceRoleHint(
          status.Value.Source);
      InputStatusLabel.Text =
        $"RX: {state} · {status.Value.DeviceName}";
      SourceBox.AccessibleDescription =
        CwAudioSourceController.SourceDisplayName(status.Value.Source) +
        (string.IsNullOrEmpty(role) ? "" : $" · {role}");
    }

    private void RefreshWorkerStatus()
    {
      CwReceiveWorker? worker =
        ctx.CwReceiveWorker;
      if (worker == null)
      {
        ModelStatusLabel.Text =
          "DeepCW: worker unavailable";
        WorkerStatusLabel.Text =
          "Worker: stopped";
        return;
      }

      CwReceiveWorkerStatus status =
        worker.GetStatus();

      string modelText =
        status.ModelState switch
        {
          CwReceiveModelState.Ready =>
            "ready",
          CwReceiveModelState.NotInstalled =>
            "not installed",
          CwReceiveModelState.Error =>
            "error",
          _ =>
            modelInstalled
              ? "installed / idle"
              : "not installed"
        };

      ModelStatusLabel.Text =
        "DeepCW: " + modelText +
        (status.InferenceBusy
          ? " · decoding"
          : string.Empty);

      if (!string.IsNullOrWhiteSpace(
            status.LastError))
        ModelStatusLabel.Text +=
          " · " + status.LastError;

      CwDenoiseMode cleanup =
        ctx.Settings.CwConsole.SpectrumDenoiseMode;
      SpectrumStatusLabel.Text =
        cleanup == CwDenoiseMode.Bypass
          ? "Spectrum: Raw"
          : $"Spectrum: {(
              cleanup == CwDenoiseMode.HamNoiseV2
                ? "HamNoise CW V2"
                : "HamNoise Classic")} · display only";

      WorkerStatusLabel.Text =
        $"Worker: tracks {status.TrackCount} · " +
        $"decode {status.CompletedInferenceWindows} · " +
        $"skipped {status.SkippedInferenceWindows} · " +
        $"gen {status.TimelineGeneration}";

      InstallModelBtn.Visible =
        status.ModelState ==
          CwReceiveModelState.NotInstalled ||
        (!modelInstalled &&
         status.ModelState ==
           CwReceiveModelState.Unknown);
    }

    private void RefreshPileupCards()
    {
      CwSignalTrack[] tracks;
      Dictionary<CwConsoleLaneIdentity, CwTranscriptSnapshot> textByLane;
      lock (stateSync)
      {
        tracks = latestTracks.ToArray();
        textByLane = new(transcripts);
      }

      IReadOnlyList<CwConsoleLaneSlot> slots =
        laneSlots.Update(tracks, DateTime.UtcNow);

      if (!slots.Any(slot =>
        slot.Identity.HasValue &&
        slot.Identity == selectedIdentity))
      {
        selectedIdentity = slots
          .FirstOrDefault(slot => slot.Present &&
            slot.Identity.HasValue).Identity;
      }

      PileupList.UpdateLanes(
        slots, textByLane, selectedIdentity);

      // One RX-only readout follows the selected stable H/T identity,
      // regardless of whether it was selected from a card or waterfall.
      // Do not route decoded text to the keyer or change any RF VFO.
      CwConsoleLaneSlot? selectedSlot = null;
      CwTranscriptSnapshot? selectedTranscript = null;
      foreach (CwConsoleLaneSlot slot in slots)
      {
        if (!slot.Identity.HasValue ||
            slot.Identity != selectedIdentity)
          continue;

        selectedSlot = slot;
        textByLane.TryGetValue(
          slot.Identity.Value, out selectedTranscript);
        break;
      }
      SelectedRxView.UpdateLane(
        selectedSlot, selectedTranscript);
    }

    private void PileupList_LaneSelected(
      CwConsoleLaneIdentity identity)
    {
      selectedIdentity = identity;
      // Card-to-spectrum selection follows the existing RX carrier only.
      // This never tunes an RF VFO or changes the decoder's full AF span.
      CwSignalTrack? selected = null;
      lock (stateSync)
        selected = CwConsolePresentation
          .CollapseDuplicateLaneIdentities(latestTracks)
          .Where(t => CwConsolePresentation.Identity(t) == identity)
          .Select(t => (CwSignalTrack?)t)
          .FirstOrDefault();

      if (selected is CwSignalTrack track &&
          WaterfallView.ViewportZoom > 1.0 &&
          (track.FrequencyHz < WaterfallView.VisibleMinimumHz ||
           track.FrequencyHz > WaterfallView.VisibleMaximumHz))
      {
        double fullMin = SpectrumAnalyzer.MinFrequencyHz;
        double fullMax = SpectrumAnalyzer.MaxFrequencyHz;
        double span = fullMax - fullMin;
        double visible = span / WaterfallView.ViewportZoom;
        double room = span - visible;
        if (room > 0)
          WaterfallView.SetViewport(
            Math.Clamp(
              (track.FrequencyHz - visible / 2 - fullMin) / room,
              0, 1),
            WaterfallView.ViewportZoom);
      }

      RefreshPileupCards();
      RefreshWaterfallSelection();
    }

    private void RefreshWaterfallSelection()
    {
      CwSignalTrack[] tracks;
      lock (stateSync)
        tracks = CwConsolePresentation
          .CollapseDuplicateLaneIdentities(latestTracks);
      WaterfallView.SetTracks(tracks, selectedIdentity);
    }

    private void WaterfallView_LaneClicked(
      object? sender,
      CwWaterfallLaneClickedEventArgs e)
    {
      selectedIdentity = e.Identity;
      RefreshPileupCards();
      RefreshWaterfallSelection();
    }

    private void RefreshWaterfall()
    {
      CwReceiveWorker? worker =
        ctx.CwReceiveWorker;
      CwAudioSourceController? audio =
        ctx.CwAudio;
      if (worker == null ||
          audio == null)
        return;

      CwReceiveWorkerStatus workerStatus =
        worker.GetStatus();

      if (workerStatus.TimelineGeneration !=
          waterfallGeneration)
      {
        waterfallGeneration =
          workerStatus.TimelineGeneration;
        lastWaterfallSampleIndex = -1;
        spectrumFrameDivider = 0;
        WaterfallView.Clear();
      }

      CwSignalTrack[] trackSnapshot;
      lock (stateSync)
        trackSnapshot =
          CwConsolePresentation
            .CollapseDuplicateLaneIdentities(
              latestTracks);

      WaterfallView.SetTracks(
        trackSnapshot,
        selectedIdentity);

      CwAudioHub hub =
        audio.Ingress.FrontEnd.Audio;

      double snapshotSeconds =
        Math.Max(
          0.20,
          SpectrumAnalyzer.FftSize /
            (double)SpectrumAnalyzer.SampleRate +
          0.02);
      if (!audio.Ingress.Enabled ||
          !hub.TrySnapshot(
            snapshotSeconds,
            out CwAudioSnapshot snapshot) ||
          snapshot.EndSampleIndex ==
            lastWaterfallSampleIndex)
        return;

      try
      {
        CwAudioSnapshot displaySnapshot =
          PrepareSpectrumDisplaySnapshot(
            snapshot);
        // The long 8192-point live spectrum need not be recomputed
        // for every 2048-point waterfall time column. Keep ~20 Hz
        // waterfall motion while budgeting the spectrum trace at ~5 Hz.
        if (spectrumFrameDivider++ % 4 == 0)
          WaterfallView.SetSpectrum(
            SpectrumAnalyzer.Analyze(displaySnapshot));
        CwAudioSpectrumFrame waterfallFrame =
          WaterfallAnalyzer.Analyze(displaySnapshot);
        lastWaterfallSampleIndex =
          snapshot.EndSampleIndex;
        WaterfallView.Append(
          waterfallFrame);
      }
      catch (ArgumentException)
      {
        // Source/timeline may have reset between status polling and snapshot
        // analysis. The next UI tick will retry on the new generation.
      }
    }

    private CwAudioSnapshot PrepareSpectrumDisplaySnapshot(
      CwAudioSnapshot raw)
    {
      CwDenoiseMode mode =
        ctx.Settings.CwConsole.SpectrumDenoiseMode;
      if (mode == CwDenoiseMode.Bypass)
        return raw;

      if (displayDenoiser == null ||
          displayDenoiserMode != mode)
      {
        displayDenoiser =
          new HamNoiseAudioDenoiser(mode);
        displayDenoiserMode = mode;
      }

      // The HamNoise bridge runs at 9.6 kHz. Resample only this immutable
      // display copy down and back up; the receive worker, ridge scanner,
      // tracker, DeepCW and transcript coordinator never see these samples.
      float[] modelRate =
        CwWindowedSincResampler.Resample(
          raw.Samples,
          raw.SampleRate,
          displayDenoiser.SampleRate);
      float[] cleaned =
        displayDenoiser.Process(
          modelRate,
          displayDenoiser.SampleRate,
          wet: 1.0);
      float[] restored =
        CwWindowedSincResampler.Resample(
          cleaned,
          displayDenoiser.SampleRate,
          raw.SampleRate);

      if (restored.Length != raw.Samples.Length)
        Array.Resize(
          ref restored,
          raw.Samples.Length);

      return new CwAudioSnapshot(
        raw.SampleRate,
        raw.EndUtc,
        raw.EndSampleIndex,
        restored);
    }

    private void RxToggleBtn_Click(
      object? sender,
      EventArgs e)
    {
      CwConsoleSettings settings =
        ctx.Settings.CwConsole;
      settings.ReceiveEnabled =
        !settings.ReceiveEnabled;
      ctx.CwAudio?.ApplySettings();
      ctx.Settings.SaveToFile();
      RefreshUi();
    }

    private void SpectrumCleanupBox_SelectedIndexChanged(
      object? sender,
      EventArgs e)
    {
      if (updatingSourceUi ||
          SpectrumCleanupBox.SelectedItem is not
            CwDenoiseMode mode)
        return;

      if (mode != CwDenoiseMode.Bypass &&
          !HamNoiseAudioDenoiser.IsAvailable())
      {
        updatingSourceUi = true;
        try
        {
          SpectrumCleanupBox.SelectedItem =
            CwDenoiseMode.Bypass;
        }
        finally
        {
          updatingSourceUi = false;
        }

        ctx.Settings.CwConsole.SpectrumDenoiseMode =
          CwDenoiseMode.Bypass;
        displayDenoiser = null;
        displayDenoiserMode =
          CwDenoiseMode.Bypass;
        ctx.Settings.SaveToFile();

        MessageBox.Show(
          this,
          "HamNoise spectrum cleanup is not available in this build. " +
          "The display has been returned to Raw. Decode always continues " +
          "from untouched PCM.",
          "CW Spectrum Cleanup",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information);
        RefreshUi();
        return;
      }

      ctx.Settings.CwConsole.SpectrumDenoiseMode =
        mode;
      displayDenoiser = null;
      displayDenoiserMode =
        CwDenoiseMode.Bypass;
      WaterfallView.Clear();
      lastWaterfallSampleIndex = -1;
      ctx.Settings.SaveToFile();
      RefreshUi();
    }

    private void SourceBox_SelectedIndexChanged(
      object? sender,
      EventArgs e)
    {
      if (updatingSourceUi ||
          SourceBox.SelectedItem is not
            CwReceiveAudioSource source)
        return;

      if (ctx.Settings.CwConsole.AudioSource ==
          source)
        return;

      ctx.Settings.CwConsole.AudioSource =
        source;
      ctx.CwAudio?.ApplySettings();
      ctx.Settings.SaveToFile();
      RefreshUi();
    }

    private async void InstallModelBtn_Click(
      object? sender,
      EventArgs e)
    {
      if (modelInstallStop != null)
        return;

      modelInstallStop =
        new CancellationTokenSource();
      InstallModelBtn.Enabled = false;

      var progress =
        new Progress<DeepCwInstallProgress>(
          value =>
          {
            if (IsDisposed)
              return;

            if (value.TotalBytes is long total &&
                total > 0)
            {
              double percent =
                100.0 *
                value.BytesReceived /
                total;
              ModelStatusLabel.Text =
                $"DeepCW: installing {value.Stage} " +
                $"{percent:F0}%";
            }
            else
            {
              ModelStatusLabel.Text =
                $"DeepCW: installing {value.Stage}";
            }
          });

      try
      {
        await DeepCwModelManager.InstallAsync(
          progress,
          modelInstallStop.Token);
        modelInstalled = true;
        ModelStatusLabel.Text =
          "DeepCW: installed";
      }
      catch (OperationCanceledException)
      {
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message,
          "DeepCW model installation failed",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
      finally
      {
        modelInstallStop.Dispose();
        modelInstallStop = null;
        if (!IsDisposed)
        {
          InstallModelBtn.Enabled = true;
          RefreshUi();
        }
      }
    }

    private void RefreshTransmitUi()
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      CwConsoleSettings settings =
        ctx.Settings.CwConsole;

      string txCapability =
        settings.TransmitEnabled
          ? "TX enabled"
          : "TX disabled";
      Text = $"CW Console [{txCapability}]";
      ctx.MainForm.UpdateCwConsoleMenuText();

      if (tx == null)
      {
        TxStatusLabel.Text =
          "TX: controller unavailable";
        SendTxBtn.Enabled = false;
        StopTxBtn.Enabled = false;
        TxTextBox.ReadOnly = true;
        return;
      }

      CwTransmitState state =
        tx.State;

      SendTxBtn.Enabled =
        settings.TransmitEnabled &&
        !state.Sending &&
        !sendRequestInProgress &&
        !string.IsNullOrWhiteSpace(
          TxTextBox.Text);

      StopTxBtn.Enabled =
        state.Sending || sendRequestInProgress;

      SetKeySpeedBtn.Enabled =
        settings.TransmitEnabled &&
        !state.Sending &&
        !statusPollInProgress;
      KeySpeedBox.Enabled =
        settings.TransmitEnabled &&
        !state.Sending;

      if (state.RadioStatus is CwKeyerStatus speedStatus &&
          !KeySpeedBox.Focused)
      {
        decimal displayed =
          Math.Clamp(
            (decimal)speedStatus.Wpm,
            KeySpeedBox.Minimum,
            KeySpeedBox.Maximum);
        KeySpeedBox.Value =
          Math.Round(displayed, 1);
      }

      TxTextBox.ReadOnly =
        state.Sending;

      foreach (Button macroButton
        in MacroButtons)
        macroButton.Enabled =
          !state.Sending;

      if (!settings.TransmitEnabled)
      {
        TxStatusLabel.Text =
          "TX: disabled in Settings";
        TxStatusLabel.ForeColor =
          SystemColors.GrayText;
        return;
      }

      if (state.Sending)
      {
        string timing =
          state.RadioStatus is
            CwKeyerStatus status
            ? $" · {status.Wpm:F1} WPM" +
              FormatTxFrequencyGuard(
                tx,
                status)
            : string.Empty;

        string watchdog =
          state.WatchdogDueUtc is
            DateTime due
            ? $" · STOP watchdog {Math.Max(0, (due - DateTime.UtcNow).TotalSeconds):F1}s"
            : string.Empty;

        TxStatusLabel.Text =
          $"TX: SENDING \"{state.ActiveText}\"" +
          timing +
          watchdog;
        TxStatusLabel.ForeColor =
          Color.Firebrick;
        return;
      }

      string radio =
        state.RadioStatus is
          CwKeyerStatus ready
          ? $" · {ready.Mode} · BK-IN {ready.BreakIn} · {ready.Wpm:F1} WPM" +
            FormatTxFrequencyGuard(
              tx,
              ready)
          : string.Empty;

      TxStatusLabel.Text =
        state.Armed
          ? "TX: ready" + radio
          : "TX: idle · Send automatically checks SkyCAT" + radio;

      if (!string.IsNullOrWhiteSpace(
            state.LastError))
        TxStatusLabel.Text +=
          " · " +
          state.LastError;

      TxStatusLabel.ForeColor =
        state.Armed
          ? Theme.SpectrumPeak
          : SystemColors.ControlText;
    }

    private static string FormatTxFrequencyGuard(
      CwTransmitController tx,
      CwKeyerStatus status)
    {
      CwTransmitInterlockSnapshot? guard =
        tx.CurrentInterlock;

      if (guard is not
            CwTransmitInterlockSnapshot value ||
          !value.IsSatellite)
        return string.Empty;

      string actual =
        status.ActualTxFrequencyHz.HasValue
          ? status.ActualTxFrequencyHz.Value
              .ToString("N0")
          : "?";

      return
        $" · TX VFO {actual} → " +
        $"{value.ExpectedCatTxHz:N0} Hz";
    }

    private async void SendTxBtn_Click(
      object? sender,
      EventArgs e) =>
      await SendTextAsync(
        TxTextBox.Text);

    private async Task SendTextAsync(
      string text)
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null ||
          sendRequestInProgress)
        return;

      // WinForms may deliver repeated Shift+Fn key messages while the first
      // asynchronous preflight is still awaiting SkyCAT. Keep only one UI
      // send request in flight. STOP remains independent and always available.
      sendRequestInProgress = true;
      SendTxBtn.Enabled = false;
      using var requestStop = new CancellationTokenSource();
      sendRequestStop = requestStop;

      try
      {
        // Capture the satellite/TX interlock at the operator's explicit
        // send action, not when the Console was opened minutes earlier.
        if (tx.State.Sending)
          throw new InvalidOperationException(
            "A CW message is already in progress. Press STOP first.");
        // A previous Send may have completed via watchdog. Always capture
        // a fresh interlock for this explicit action, never reuse an old
        // satellite/no-Doppler context across sends.
        tx.Arm();
        await tx.SendAsync(text, requestStop.Token);
      }
      catch (OperationCanceledException) when (requestStop.IsCancellationRequested)
      {
        // The STOP button cancelled a pending preflight/send request.
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message,
          "CW send failed",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
      finally
      {
        sendRequestStop = null;
        sendRequestInProgress = false;
        if (!IsDisposed)
          RefreshTransmitUi();
      }
    }

    private async void SetKeySpeedBtn_Click(
      object? sender,
      EventArgs e)
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null || statusPollInProgress)
        return;

      SetKeySpeedBtn.Enabled = false;
      statusPollInProgress = true;
      try
      {
        CwKeyerStatus status =
          await tx.SetKeySpeedAsync(
            (double)KeySpeedBox.Value);
        KeySpeedBox.Value =
          Math.Round(
            Math.Clamp(
              (decimal)status.Wpm,
              KeySpeedBox.Minimum,
              KeySpeedBox.Maximum),
            1);
        nextStatusPollUtc =
          DateTime.UtcNow.AddSeconds(1);
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message,
          "CW WPM control failed",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
      finally
      {
        statusPollInProgress = false;
        if (!IsDisposed)
          RefreshTransmitUi();
      }
    }

    private void PollTransmitStatusIfDue()
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null ||
          statusPollInProgress ||
          !ctx.Settings.CwConsole.TransmitEnabled ||
          !tx.State.Armed ||
          tx.State.Sending ||
          DateTime.UtcNow < nextStatusPollUtc)
        return;

      statusPollInProgress = true;
      nextStatusPollUtc =
        DateTime.UtcNow.AddSeconds(1);
      _ = PollTransmitStatusAsync(tx);
    }

    private async Task PollTransmitStatusAsync(
      CwTransmitController tx)
    {
      try
      {
        await tx.QueryStatusAsync();
      }
      catch
      {
        // QueryStatusAsync preserves the error in controller state. Keep the
        // UI responsive and retry on the next 1 Hz poll while armed/idle.
      }
      finally
      {
        statusPollInProgress = false;
        if (!IsDisposed)
          RefreshTransmitUi();
      }
    }

    private async void StopTxBtn_Click(
      object? sender,
      EventArgs e)
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null)
        return;

      StopTxBtn.Enabled = false;
      // STOP is also actionable while an asynchronous preflight is waiting:
      // cancel the request before taking the keyer session lock.
      sendRequestStop?.Cancel();

      try
      {
        await tx.DisarmAsync();
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message +
          "\r\n\r\nThe TCP lease was closed so SkyCAT can run its disconnect fail-safe STOP.",
          "CW STOP failed",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
      finally
      {
        if (!IsDisposed)
          RefreshTransmitUi();
      }
    }

    private void LoadSettingsIntoControls()
    {
      updatingSourceUi = true;
      try
      {
        SourceBox.SelectedItem =
          ctx.Settings.CwConsole.AudioSource;
        SpectrumCleanupBox.SelectedItem =
          ctx.Settings.CwConsole.SpectrumDenoiseMode;
      }
      finally
      {
        updatingSourceUi = false;
      }

      RefreshMacroButtons();
      RefreshUi();
    }

    private void RefreshMacroButtons()
    {
      CwMacroSettings macros =
        ctx.Settings.CwConsole.Macros;

      for (int i = 0;
           i < MacroButtons.Length;
           i++)
      {
        Button button =
          MacroButtons[i];
        button.Text =
          CwMacroBank.Preview(
            macros,
            i);

        string raw =
          CwMacroBank.Get(
            macros,
            i);
        MacroToolTip.SetToolTip(
          button,
          string.IsNullOrWhiteSpace(raw)
            ? $"F{i + 1} is empty. Right-click to edit."
            : $"F{i + 1}: load · right-click: edit\r\nShift+F{i + 1}: send\r\n\r\n{raw}");
      }
    }

    private void EditMacro(int index)
    {
      string initial = CwMacroBank.Get(
        ctx.Settings.CwConsole.Macros, index);

      using var dialog = new Form
      {
        Text = $"Edit CW macro F{index + 1}",
        StartPosition = FormStartPosition.CenterParent,
        ClientSize = new Size(455, 185),
        MinimumSize = new Size(300, 160),
        FormBorderStyle = FormBorderStyle.SizableToolWindow
      };
      var input = new TextBox
      {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        MaxLength = CwMessageTiming.MaxCharacters,
        Text = initial,
        Font = new Font(FontFamily.GenericMonospace, 10f)
      };
      var footer = new FlowLayoutPanel
      {
        Dock = DockStyle.Bottom,
        FlowDirection = FlowDirection.RightToLeft,
        Height = 40,
        Padding = new Padding(4)
      };
      var cancel = new Button
      {
        Text = "Cancel",
        DialogResult = DialogResult.Cancel,
        AutoSize = true
      };
      var save = new Button
      {
        Text = "Save",
        AutoSize = true
      };
      save.Click += (_, _) =>
      {
        try
        {
          string value = input.Text.Trim().ToUpperInvariant();
          // Validate before changing the persistent preset.
          if (value.Length > 0)
            CwMessageTiming.ValidateText(value);
          CwMacroBank.Set(ctx.Settings.CwConsole.Macros, index, value);
          ctx.Settings.SaveToFile();
          dialog.DialogResult = DialogResult.OK;
          dialog.Close();
        }
        catch (Exception ex)
        {
          MessageBox.Show(dialog, ex.Message, "CW macro",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
      };
      footer.Controls.Add(cancel);
      footer.Controls.Add(save);
      dialog.CancelButton = cancel;
      dialog.Controls.Add(input);
      dialog.Controls.Add(footer);
      if (dialog.ShowDialog(this) == DialogResult.OK)
        RefreshMacroButtons();
    }

    private void LoadMacroIntoComposer(
      int index)
    {
      try
      {
        string text =
          CwMacroBank.Prepare(
            ctx.Settings.CwConsole.Macros,
            index);

        TxTextBox.Text = text;
        TxTextBox.SelectionStart =
          TxTextBox.TextLength;
        TxTextBox.Focus();
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message,
          $"CW macro F{index + 1}",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
      }
    }

    private async Task SendMacroAsync(
      int index)
    {
      try
      {
        string text =
          CwMacroBank.Prepare(
            ctx.Settings.CwConsole.Macros,
            index);

        TxTextBox.Text = text;
        await SendTextAsync(text);
      }
      catch (Exception ex)
      {
        MessageBox.Show(
          this,
          ex.Message,
          $"CW macro F{index + 1}",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
      }
    }

    protected override bool ProcessCmdKey(
      ref Message msg,
      Keys keyData)
    {
      Keys key =
        keyData & Keys.KeyCode;

      if (key >= Keys.F1 &&
          key <= Keys.F8)
      {
        Keys modifiers =
          keyData & Keys.Modifiers;

        // Only the two documented forms are owned by the CW Console:
        //   F1..F8       -> load only
        //   Shift+F1..F8 -> explicit send
        // Ctrl/Alt combinations are deliberately left to WinForms/the host
        // so an unrelated shortcut can never be reinterpreted as transmit.
        if (modifiers is not (
              Keys.None or
              Keys.Shift))
          return base.ProcessCmdKey(
            ref msg,
            keyData);

        int index =
          (int)key -
          (int)Keys.F1;

        if (modifiers == Keys.Shift)
          _ = SendMacroAsync(index);
        else
          LoadMacroIntoComposer(index);

        return true;
      }

      return base.ProcessCmdKey(
        ref msg,
        keyData);
    }

    private void CwConsolePanel_FormClosing(
      object? sender,
      FormClosingEventArgs e)
    {
      UiTimer.Stop();
      UiTimer.Tick -=
        UiTimer_Tick;
      WaterfallTimer.Stop();
      WaterfallTimer.Tick -=
        WaterfallTimer_Tick;
      WaterfallView.LaneClicked -=
        WaterfallView_LaneClicked;
      // FFTW plans belong to the display analyzers, not the receiver;
      // release their aligned native buffers when the Console closes.
      WaterfallAnalyzer.Dispose();
      SpectrumAnalyzer.Dispose();

      modelInstallStop?.Cancel();

      if (ctx.CwTransmit?.State.Armed == true)
      {
        try
        {
          ctx.CwTransmit
            .DisarmAsync()
            .GetAwaiter()
            .GetResult();
        }
        catch
        {
          // Closing the keyer session invokes SkyCAT's disconnect STOP even
          // if the explicit STOP acknowledgement was lost.
        }
      }

      CwReceiveWorker? worker =
        ctx.CwReceiveWorker;
      if (worker != null)
      {
        worker.TracksUpdated -=
          Worker_TracksUpdated;
        worker.DecodeUpdated -=
          Worker_DecodeUpdated;
        worker.StatusChanged -=
          Worker_StatusChanged;
      }

      if (ReferenceEquals(
            ctx.CwConsolePanel,
            this))
        ctx.CwConsolePanel = null;

      ctx.MainForm.CwConsoleMNU.Checked =
        false;
    }
  }
}
