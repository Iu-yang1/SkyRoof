using SkyRoof.CW;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  /// <summary>
  /// View over the shared CW receive worker plus the explicitly armed,
  /// fail-closed SkyCAT Command-17 transmitter. The panel owns no audio,
  /// tracker or ONNX resource; closing it never stops RX, but it always
  /// disarms/stops any CW transmission started from this Console.
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
    private readonly CwAudioWaterfallView WaterfallView =
      new(spectrumBins: 512);
    private long waterfallGeneration = -1;
    private long lastWaterfallSampleIndex = -1;

    private readonly DataGridView LaneGrid = new();
    private readonly Label SelectedLaneLabel = new();
    private readonly TextBox CommittedTextBox = new();
    private readonly TextBox ProvisionalTextBox = new();
    private readonly Button CopyBtn = new();

    private readonly TextBox TxTextBox = new();
    private readonly Button ArmTxBtn = new();
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
    private bool statusPollInProgress;
    private DateTime nextStatusPollUtc = DateTime.MinValue;

    private readonly System.Windows.Forms.Timer UiTimer =
      new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer WaterfallTimer =
      new() { Interval = 100 };

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
      WaterfallAnalyzer =
        scanner == null
          ? new CwAudioWaterfallAnalyzer()
          : new CwAudioWaterfallAnalyzer(
              sampleRate: scanner.SampleRate,
              minFrequencyHz:
                scanner.MinFrequencyHz,
              fftSize: 8192,
              minFrequencyHz:
                scanner.MinFrequencyHz,
              maxFrequencyHz:
                scanner.MaxFrequencyHz,
              outputBins: 512);

      Text = "CW Console [TX disabled]";
      Name = "CwConsolePanel";
      ClientSize = new Size(1040, 900);
      MinimumSize = new Size(720, 720);
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
        new TableLayoutPanel
        {
          Dock = DockStyle.Fill,
          ColumnCount = 1,
          RowCount = 6,
          Padding = new Padding(8)
        };

      root.ColumnStyles.Add(
        new ColumnStyle(
          SizeType.Percent,
          100));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Absolute,
          170));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          100));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Absolute,
          190));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Absolute,
          220));

      var toolbar =
        new FlowLayoutPanel
        {
          Dock = DockStyle.Fill,
          AutoSize = true,
          WrapContents = true,
          FlowDirection =
            FlowDirection.LeftToRight,
          Margin = new Padding(
            0, 0, 0, 5)
        };

      RxToggleBtn.AutoSize = true;
      RxToggleBtn.Click +=
        RxToggleBtn_Click;
      toolbar.Controls.Add(
        RxToggleBtn);

      SourceBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SourceBox.Width = 230;
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
        "Radio USB / WASAPI capture = Windows capture/input endpoint such as Microphone (USB Audio CODEC).\r\n" +
        "RS-BA1 speaker loopback = Windows playback/render endpoint that RS-BA1 is actually playing into.");
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

      SettingsBtn.Text = "Settings…";
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
          AutoSize = true,
          WrapContents = true,
          FlowDirection =
            FlowDirection.LeftToRight,
          Margin = new Padding(
            0, 0, 0, 5)
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

      WaterfallView.Dock =
        DockStyle.Fill;
      WaterfallView.Margin =
        new Padding(
          0, 0, 0, 6);
      WaterfallView.LaneClicked +=
        WaterfallView_LaneClicked;
      root.Controls.Add(
        WaterfallView,
        0,
        2);

      ConfigureLaneGrid();
      root.Controls.Add(
        LaneGrid,
        0,
        3);

      root.Controls.Add(
        BuildTranscriptPanel(),
        0,
        4);

      root.Controls.Add(
        BuildTransmitPanel(),
        0,
        5);

      Controls.Add(root);
    }

    private void ConfigureLaneGrid()
    {
      LaneGrid.Dock = DockStyle.Fill;
      LaneGrid.ReadOnly = true;
      LaneGrid.AllowUserToAddRows = false;
      LaneGrid.AllowUserToDeleteRows = false;
      LaneGrid.AllowUserToResizeRows = false;
      LaneGrid.AllowUserToOrderColumns = false;
      LaneGrid.MultiSelect = false;
      LaneGrid.SelectionMode =
        DataGridViewSelectionMode.FullRowSelect;
      LaneGrid.AutoGenerateColumns = false;
      LaneGrid.AutoSizeColumnsMode =
        DataGridViewAutoSizeColumnsMode.Fill;
      LaneGrid.RowHeadersVisible = false;
      LaneGrid.BackgroundColor =
        SystemColors.Window;
      LaneGrid.BorderStyle =
        BorderStyle.FixedSingle;

      LaneGrid.Columns.Add(
        MakeTextColumn(
          "Lane",
          "Slot / Lane",
          76,
          0.45f));
      LaneGrid.Columns.Add(
        MakeTextColumn(
          "Frequency",
          "AF Hz",
          82,
          0.72f));
      LaneGrid.Columns.Add(
        MakeTextColumn(
          "Snr",
          "SNR dB",
          74,
          0.62f));
      LaneGrid.Columns.Add(
        MakeTextColumn(
          "Drift",
          "Hz/s",
          72,
          0.62f));
      LaneGrid.Columns.Add(
        MakeTextColumn(
          "State",
          "State",
          92,
          0.82f));
      LaneGrid.Columns.Add(
        MakeTextColumn(
          "Identity",
          "ID conf",
          76,
          0.64f));

      var textColumn =
        MakeTextColumn(
          "Transcript",
          "Transcript",
          260,
          3.4f);
      textColumn.DefaultCellStyle.Font =
        new Font(
          FontFamily.GenericMonospace,
          9.5f);
      LaneGrid.Columns.Add(
        textColumn);

      LaneGrid.SelectionChanged +=
        LaneGrid_SelectionChanged;
    }

    private static DataGridViewTextBoxColumn
      MakeTextColumn(
        string name,
        string header,
        int minimumWidth,
        float fillWeight) =>
      new()
      {
        Name = name,
        HeaderText = header,
        MinimumWidth = minimumWidth,
        FillWeight = fillWeight,
        SortMode =
          DataGridViewColumnSortMode
            .NotSortable
      };

    private Control BuildTranscriptPanel()
    {
      var group =
        new GroupBox
        {
          Dock = DockStyle.Fill,
          Text = "Selected RX Transcript",
          Padding = new Padding(8),
          MinimumSize = new Size(0, 180)
        };

      var layout =
        new TableLayoutPanel
        {
          Dock = DockStyle.Fill,
          ColumnCount = 2,
          RowCount = 5
        };

      layout.ColumnStyles.Add(
        new ColumnStyle(
          SizeType.Percent,
          100));
      layout.ColumnStyles.Add(
        new ColumnStyle(
          SizeType.AutoSize));

      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          70));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          30));

      SelectedLaneLabel.Text =
        "No lane selected";
      SelectedLaneLabel.AutoSize = true;
      SelectedLaneLabel.Font =
        new Font(
          Font,
          FontStyle.Bold);
      layout.Controls.Add(
        SelectedLaneLabel,
        0,
        0);

      CopyBtn.Text = "Copy";
      CopyBtn.AutoSize = true;
      CopyBtn.Click +=
        CopyBtn_Click;
      layout.Controls.Add(
        CopyBtn,
        1,
        0);

      var committedLabel =
        new Label
        {
          Text = "Committed",
          AutoSize = true,
          Margin = new Padding(
            0, 5, 0, 2)
        };
      layout.SetColumnSpan(
        committedLabel,
        2);
      layout.Controls.Add(
        committedLabel,
        0,
        1);

      ConfigureTranscriptBox(
        CommittedTextBox);
      layout.SetColumnSpan(
        CommittedTextBox,
        2);
      layout.Controls.Add(
        CommittedTextBox,
        0,
        2);

      var provisionalLabel =
        new Label
        {
          Text = "Provisional / may change",
          AutoSize = true,
          ForeColor = Theme.ScaleAccent,
          Margin = new Padding(
            0, 5, 0, 2)
        };
      layout.SetColumnSpan(
        provisionalLabel,
        2);
      layout.Controls.Add(
        provisionalLabel,
        0,
        3);

      ConfigureTranscriptBox(
        ProvisionalTextBox);
      ProvisionalTextBox.ForeColor =
        Theme.ScaleAccent;
      layout.SetColumnSpan(
        ProvisionalTextBox,
        2);
      layout.Controls.Add(
        ProvisionalTextBox,
        0,
        4);

      group.Controls.Add(
        layout);
      return group;
    }

    private Control BuildTransmitPanel()
    {
      var group =
        new GroupBox
        {
          Dock = DockStyle.Fill,
          Text =
            "CW Transmit — IC-9700 / SkyCAT Command 17",
          Padding = new Padding(8),
          Margin = new Padding(
            0, 6, 0, 0)
        };

      var layout =
        new TableLayoutPanel
        {
          Dock = DockStyle.Fill,
          ColumnCount = 1,
          RowCount = 4
        };

      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          100));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));
      layout.RowStyles.Add(
        new RowStyle(
          SizeType.AutoSize));

      TxStatusLabel.AutoSize = true;
      TxStatusLabel.Text =
        "TX: disabled";
      TxStatusLabel.Margin =
        new Padding(
          0, 0, 0, 5);
      layout.Controls.Add(
        TxStatusLabel,
        0,
        0);

      TxTextBox.Dock =
        DockStyle.Fill;
      TxTextBox.Multiline = true;
      TxTextBox.MaxLength =
        CwMessageTiming.MaxCharacters;
      TxTextBox.Font =
        new Font(
          FontFamily.GenericMonospace,
          11f);
      TxTextBox.ScrollBars =
        ScrollBars.Vertical;
      TxTextBox.TextChanged +=
        (_, _) =>
          RefreshTransmitUi();
      layout.Controls.Add(
        TxTextBox,
        0,
        1);

      var macros =
        new FlowLayoutPanel
        {
          AutoSize = true,
          Dock = DockStyle.Fill,
          FlowDirection =
            FlowDirection.LeftToRight,
          WrapContents = true,
          Margin = new Padding(
            0, 5, 0, 0)
        };

      for (int i = 0;
           i < MacroButtons.Length;
           i++)
      {
        Button button =
          MacroButtons[i];
        int index = i;

        button.AutoSize = true;
        button.Tag = index;
        button.Click +=
          (_, _) =>
            LoadMacroIntoComposer(
              index);
        macros.Controls.Add(
          button);
      }

      macros.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text =
            "F1–F8 load · Shift+F1–F8 send",
          Margin = new Padding(
            8, 7, 0, 0)
        });

      layout.Controls.Add(
        macros,
        0,
        2);

      var buttons =
        new FlowLayoutPanel
        {
          AutoSize = true,
          Dock = DockStyle.Fill,
          FlowDirection =
            FlowDirection.LeftToRight,
          WrapContents = true,
          Margin = new Padding(
            0, 5, 0, 0)
        };

      ArmTxBtn.Text =
        "Arm TX";
      ArmTxBtn.AutoSize = true;
      ArmTxBtn.Click +=
        ArmTxBtn_Click;
      buttons.Controls.Add(
        ArmTxBtn);

      SendTxBtn.Text =
        "Send";
      SendTxBtn.AutoSize = true;
      SendTxBtn.Click +=
        SendTxBtn_Click;
      buttons.Controls.Add(
        SendTxBtn);

      StopTxBtn.Text =
        "STOP";
      StopTxBtn.AutoSize = true;
      StopTxBtn.BackColor =
        Color.Firebrick;
      StopTxBtn.ForeColor =
        Color.White;
      StopTxBtn.UseVisualStyleBackColor =
        false;
      StopTxBtn.Click +=
        StopTxBtn_Click;
      buttons.Controls.Add(
        StopTxBtn);

      buttons.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "Speed",
          Margin = new Padding(
            12, 7, 2, 0)
        });

      KeySpeedBox.Minimum =
        (decimal)CwMessageTiming.MinimumWpm;
      KeySpeedBox.Maximum =
        (decimal)CwMessageTiming.MaximumWpm;
      KeySpeedBox.DecimalPlaces = 1;
      KeySpeedBox.Increment = 0.5m;
      KeySpeedBox.Value = 20m;
      KeySpeedBox.Width = 64;
      buttons.Controls.Add(
        KeySpeedBox);

      SetKeySpeedBtn.Text = "Set WPM";
      SetKeySpeedBtn.AutoSize = true;
      SetKeySpeedBtn.Click +=
        SetKeySpeedBtn_Click;
      buttons.Controls.Add(
        SetKeySpeedBtn);

      buttons.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text =
            "Max 30 chars · radio must already be CW/CW-R with Semi/Full BK-IN",
          Margin = new Padding(
            12, 7, 0, 0)
        });

      layout.Controls.Add(
        buttons,
        0,
        3);

      group.Controls.Add(
        layout);
      return group;
    }

    private static void ConfigureTranscriptBox(
      TextBox box)
    {
      box.Dock = DockStyle.Fill;
      box.Multiline = true;
      box.ReadOnly = true;
      box.ScrollBars =
        ScrollBars.Vertical;
      box.Font =
        new Font(
          FontFamily.GenericMonospace,
          11f);
      box.BackColor =
        SystemColors.Window;
      box.BorderStyle =
        BorderStyle.FixedSingle;
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
      RefreshGrid();
      RefreshSelectedTranscript();
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
        $"Input: {CwAudioSourceController.SourceDisplayName(status.Value.Source)} · {state} · " +
        $"{status.Value.DeviceName}" +
        (string.IsNullOrEmpty(role)
          ? string.Empty
          : $" · {role}");
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

    private void RefreshGrid()
    {
      CwSignalTrack[] tracks;
      Dictionary<
        CwConsoleLaneIdentity,
        CwTranscriptSnapshot> textByLane;

      lock (stateSync)
      {
        tracks = latestTracks.ToArray();
        textByLane = new(transcripts);
      }

      IReadOnlyList<CwConsoleLaneSlot> slots =
        laneSlots.Update(
          tracks,
          DateTime.UtcNow);

      CwConsoleLaneIdentity? preserve =
        SelectedRowIdentity() ??
        selectedIdentity;

      LaneGrid.SuspendLayout();
      try
      {
        while (LaneGrid.Rows.Count < laneSlots.MaxSlots)
          LaneGrid.Rows.Add();
        while (LaneGrid.Rows.Count > laneSlots.MaxSlots)
          LaneGrid.Rows.RemoveAt(
            LaneGrid.Rows.Count - 1);

        foreach (CwConsoleLaneSlot slot in slots)
        {
          DataGridViewRow row =
            LaneGrid.Rows[slot.Index];

          if (slot.Identity is not CwConsoleLaneIdentity identity ||
              slot.Track is not CwSignalTrack track)
          {
            ClearLaneRow(row, slot.Index);
            continue;
          }

          row.Tag = identity;
          textByLane.TryGetValue(
            identity,
            out CwTranscriptSnapshot transcript);

          UpdateLaneRow(
            row,
            slot.Index,
            track,
            transcript,
            slot.Present);
        }

        DataGridViewRow? select = null;
        if (preserve.HasValue)
          select =
            LaneGrid.Rows
              .Cast<DataGridViewRow>()
              .FirstOrDefault(row =>
                row.Tag is CwConsoleLaneIdentity identity &&
                identity == preserve.Value);

        select ??=
          slots
            .Where(slot => slot.Present && slot.Identity.HasValue)
            .Select(slot => LaneGrid.Rows[slot.Index])
            .FirstOrDefault();

        if (select != null &&
            select.Tag is CwConsoleLaneIdentity selectIdentity)
        {
          if (SelectedRowIdentity() != selectIdentity)
          {
            LaneGrid.ClearSelection();
            select.Selected = true;
            LaneGrid.CurrentCell = select.Cells[0];
          }
          selectedIdentity = selectIdentity;
        }
        else
        {
          LaneGrid.ClearSelection();
          selectedIdentity = null;
        }
      }
      finally
      {
        LaneGrid.ResumeLayout();
      }
    }

    private static void ClearLaneRow(
      DataGridViewRow row,
      int slotIndex)
    {
      row.Tag = null;
      row.Cells[0].Value =
        $"{slotIndex + 1} · —";
      for (int i = 1; i < row.Cells.Count; i++)
        row.Cells[i].Value = string.Empty;
      row.DefaultCellStyle.ForeColor =
        SystemColors.GrayText;
    }

    private static void UpdateLaneRow(
      DataGridViewRow row,
      int slotIndex,
      CwSignalTrack track,
      CwTranscriptSnapshot? transcript,
      bool present)
    {
      row.Cells[0].Value =
        $"{slotIndex + 1} · " +
        CwConsolePresentation.LaneLabel(track);
      row.Cells[1].Value =
        track.FrequencyHz.ToString("F1");
      row.Cells[2].Value =
        track.SnrDb.ToString("F1");
      row.Cells[3].Value =
        track.DriftHzPerSecond.ToString(
          "+0.0;-0.0;0.0");
      row.Cells[4].Value =
        present
          ? CwConsolePresentation.StateText(track)
          : "Grace";
      row.Cells[5].Value =
        track.IdentityConfidence.ToString("P0");
      row.Cells[6].Value =
        CwConsolePresentation.GridTranscript(transcript);

      row.DefaultCellStyle.ForeColor =
        !present
          ? SystemColors.GrayText
          : track.Ambiguous
            ? Theme.SpectrumPeak
            : !track.Active
              ? SystemColors.GrayText
              : SystemColors.ControlText;
    }

    private void LaneGrid_SelectionChanged(
      object? sender,
      EventArgs e)
    {
      CwConsoleLaneIdentity? identity =
        SelectedRowIdentity();
      if (identity.HasValue)
        selectedIdentity =
          identity.Value;
    }

    private void WaterfallView_LaneClicked(
      object? sender,
      CwWaterfallLaneClickedEventArgs e)
    {
      selectedIdentity =
        e.Identity;

      DataGridViewRow? row =
        LaneGrid.Rows
          .Cast<DataGridViewRow>()
          .FirstOrDefault(
            value =>
              value.Tag is
                CwConsoleLaneIdentity identity &&
              identity == e.Identity);

      if (row != null)
      {
        LaneGrid.ClearSelection();
        row.Selected = true;
        LaneGrid.CurrentCell =
          row.Cells[0];
      }

      RefreshSelectedTranscript();
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
          WaterfallAnalyzer.FftSize /
            (double)WaterfallAnalyzer.SampleRate +
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
        CwAudioSpectrumFrame frame =
          WaterfallAnalyzer.Analyze(
            displaySnapshot);
        lastWaterfallSampleIndex =
          snapshot.EndSampleIndex;
        WaterfallView.Append(frame);
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

    private CwConsoleLaneIdentity?
      SelectedRowIdentity()
    {
      if (LaneGrid.SelectedRows.Count == 0)
        return null;

      return LaneGrid.SelectedRows[0].Tag is
          CwConsoleLaneIdentity identity
        ? identity
        : null;
    }

    private void RefreshSelectedTranscript()
    {
      CwConsoleLaneIdentity? identity =
        selectedIdentity;
      if (!identity.HasValue)
      {
        SelectedLaneLabel.Text =
          "No lane selected";
        CommittedTextBox.Text =
          string.Empty;
        ProvisionalTextBox.Text =
          string.Empty;
        CopyBtn.Enabled = false;
        return;
      }

      CwSignalTrack? track = null;
      CwTranscriptSnapshot? transcript = null;

      lock (stateSync)
      {
        foreach (CwSignalTrack candidate
          in CwConsolePresentation
            .CollapseDuplicateLaneIdentities(
              latestTracks))
        {
          if (CwConsolePresentation.Identity(
                candidate) ==
              identity.Value)
          {
            track = candidate;
            break;
          }
        }

        if (transcripts.TryGetValue(
              identity.Value,
              out CwTranscriptSnapshot value))
          transcript = value;
      }

      if (track.HasValue)
      {
        CwSignalTrack value =
          track.Value;
        SelectedLaneLabel.Text =
          $"Lane {CwConsolePresentation.LaneDiagnosticLabel(value)} · " +
          $"{value.FrequencyHz:F1} Hz · " +
          $"{value.SnrDb:F1} dB · " +
          CwConsolePresentation.StateText(
            value);
      }
      else
      {
        SelectedLaneLabel.Text =
          "Selected lane is currently unavailable";
      }

      CommittedTextBox.Text =
        transcript?.CommittedText ??
        string.Empty;
      ProvisionalTextBox.Text =
        transcript?.ProvisionalText ??
        string.Empty;
      CopyBtn.Enabled =
        !string.IsNullOrEmpty(
          transcript?.Text);
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

    private void CopyBtn_Click(
      object? sender,
      EventArgs e)
    {
      string text =
        CommittedTextBox.Text +
        ProvisionalTextBox.Text;
      if (!string.IsNullOrEmpty(text))
        Clipboard.SetText(text);
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
        ArmTxBtn.Enabled = false;
        SendTxBtn.Enabled = false;
        StopTxBtn.Enabled = false;
        TxTextBox.ReadOnly = true;
        return;
      }

      CwTransmitState state =
        tx.State;

      ArmTxBtn.Text =
        state.Armed
          ? "Disarm TX"
          : "Arm TX";

      ArmTxBtn.Enabled =
        settings.TransmitEnabled &&
        !state.Sending;

      SendTxBtn.Enabled =
        settings.TransmitEnabled &&
        state.Armed &&
        !state.Sending &&
        !string.IsNullOrWhiteSpace(
          TxTextBox.Text);

      StopTxBtn.Enabled =
        state.Sending;

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
          ? "TX: ARMED" + radio
          : "TX: disarmed" + radio;

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

    private async void ArmTxBtn_Click(
      object? sender,
      EventArgs e)
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null)
        return;

      ArmTxBtn.Enabled = false;

      try
      {
        if (tx.State.Armed)
        {
          await tx.DisarmAsync();
          return;
        }

        tx.Arm();

        CwKeyerStatus status =
          await tx.QueryStatusAsync();

        if (!status.ReadyToSend)
        {
          await tx.DisarmAsync();

          throw new InvalidOperationException(
            $"SkyCAT keyer is not ready: lease={status.Lease}, " +
            $"mode={status.Mode}, BK-IN={status.BreakIn}, " +
            $"TX={(status.Transmitting ? 1 : 0)}.");
        }
      }
      catch (Exception ex)
      {
        if (tx.State.Armed &&
            !tx.State.Sending)
        {
          try
          {
            await tx.DisarmAsync();
          }
          catch
          {
            // No message was active in the normal arm-preflight failure path.
            // If that ever changes, controller/session teardown remains the
            // fail-safe and the state preserves the error.
          }
        }

        MessageBox.Show(
          this,
          ex.Message,
          "CW TX arm failed",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
      }
      finally
      {
        if (!IsDisposed)
          RefreshTransmitUi();
      }
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

      try
      {
        await tx.SendAsync(text);
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

      try
      {
        await tx.StopAsync();
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
            ? $"F{i + 1} is empty. Edit it in Settings > CW Console > CW Message Macros."
            : $"F{i + 1}: load macro\r\nShift+F{i + 1}: send after explicit Arm\r\n\r\n{raw}");
      }
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
