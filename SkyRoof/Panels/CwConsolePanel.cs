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
    private readonly Button SettingsBtn = new();
    private readonly Button InstallModelBtn = new();

    private readonly Label InputStatusLabel = new();
    private readonly Label ModelStatusLabel = new();
    private readonly Label WorkerStatusLabel = new();

    private readonly CwAudioWaterfallAnalyzer WaterfallAnalyzer;
    private readonly CwAudioWaterfallView WaterfallView =
      new();
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

    private long displayGeneration = -1;
    private CwConsoleLaneIdentity? selectedIdentity;
    private bool updatingSourceUi;
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
              maxFrequencyHz:
                scanner.MaxFrequencyHz,
              outputBins: 384);

      Text = "CW Console";
      Name = "CwConsolePanel";
      ClientSize = new Size(980, 850);
      MinimumSize = new Size(680, 650);

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
          182));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          58));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Percent,
          42));
      root.RowStyles.Add(
        new RowStyle(
          SizeType.Absolute,
          158));

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
      SourceBox.Width = 156;
      SourceBox.DataSource =
        Enum.GetValues<
          CwReceiveAudioSource>();
      SourceBox.SelectedIndexChanged +=
        SourceBox_SelectedIndexChanged;
      toolbar.Controls.Add(
        SourceBox);

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
          "Lane",
          55,
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
            .Automatic
      };

    private Control BuildTranscriptPanel()
    {
      var group =
        new GroupBox
        {
          Dock = DockStyle.Fill,
          Text = "Selected RX Transcript",
          Padding = new Padding(8)
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
          RowCount = 3
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
          Text =
            "Max 30 chars · radio must already be CW/CW-R with Semi/Full BK-IN",
          Margin = new Padding(
            12, 7, 0, 0)
        });

      layout.Controls.Add(
        buttons,
        0,
        2);

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
      selectedIdentity = null;
    }

    private void UiTimer_Tick(
      object? sender,
      EventArgs e) =>
      RefreshUi();

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

      InputStatusLabel.Text =
        $"Input: {status.Value.Source} · {state} · " +
        $"{status.Value.DeviceName}";
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
        tracks =
          latestTracks
            .OrderBy(x => x.FrequencyHz)
            .ToArray();
        textByLane =
          new(transcripts);
      }

      CwConsoleLaneIdentity? preserve =
        SelectedRowIdentity() ??
        selectedIdentity;

      LaneGrid.SuspendLayout();
      try
      {
        var existingRows =
          LaneGrid.Rows
            .Cast<DataGridViewRow>()
            .Where(row =>
              row.Tag is
                CwConsoleLaneIdentity)
            .ToDictionary(
              row =>
                (CwConsoleLaneIdentity)
                  row.Tag!);

        bool sameLaneSet =
          existingRows.Count ==
            tracks.Length &&
          tracks.All(track =>
            existingRows.ContainsKey(
              CwConsolePresentation.Identity(
                track)));

        if (!sameLaneSet)
        {
          LaneGrid.Rows.Clear();
          existingRows.Clear();

          foreach (CwSignalTrack track
            in tracks)
          {
            int index =
              LaneGrid.Rows.Add();
            DataGridViewRow row =
              LaneGrid.Rows[index];
            CwConsoleLaneIdentity identity =
              CwConsolePresentation.Identity(
                track);
            row.Tag = identity;
            existingRows[identity] = row;
          }
        }

        foreach (CwSignalTrack track
          in tracks)
        {
          CwConsoleLaneIdentity identity =
            CwConsolePresentation.Identity(
              track);

          textByLane.TryGetValue(
            identity,
            out CwTranscriptSnapshot transcript);

          UpdateLaneRow(
            existingRows[identity],
            track,
            transcript);
        }

        if (tracks.Length > 0)
        {
          DataGridViewRow? select =
            preserve.HasValue &&
            existingRows.TryGetValue(
              preserve.Value,
              out DataGridViewRow? preservedRow)
              ? preservedRow
              : existingRows[
                  CwConsolePresentation.Identity(
                    tracks[0])];

          CwConsoleLaneIdentity selectIdentity =
            (CwConsoleLaneIdentity)
              select.Tag!;

          if (SelectedRowIdentity() !=
              selectIdentity)
          {
            LaneGrid.ClearSelection();
            select.Selected = true;
            LaneGrid.CurrentCell =
              select.Cells[0];
          }

          selectedIdentity =
            selectIdentity;
        }
        else
        {
          selectedIdentity = null;
        }
      }
      finally
      {
        LaneGrid.ResumeLayout();
      }
    }

    private static void UpdateLaneRow(
      DataGridViewRow row,
      CwSignalTrack track,
      CwTranscriptSnapshot? transcript)
    {
      row.Cells[0].Value =
        CwConsolePresentation.LaneLabel(
          track);
      row.Cells[1].Value =
        track.FrequencyHz.ToString("F1");
      row.Cells[2].Value =
        track.SnrDb.ToString("F1");
      row.Cells[3].Value =
        track.DriftHzPerSecond.ToString(
          "+0.0;-0.0;0.0");
      row.Cells[4].Value =
        CwConsolePresentation.StateText(
          track);
      row.Cells[5].Value =
        track.IdentityConfidence.ToString(
          "P0");
      row.Cells[6].Value =
        CwConsolePresentation.GridTranscript(
          transcript);

      row.DefaultCellStyle.ForeColor =
        track.Ambiguous
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
          latestTracks.ToArray();

      WaterfallView.SetTracks(
        trackSnapshot,
        selectedIdentity);

      CwAudioHub hub =
        audio.Ingress.FrontEnd.Audio;

      const double snapshotSeconds = 0.10;
      if (!audio.Ingress.Enabled ||
          !hub.TrySnapshot(
            snapshotSeconds,
            out CwAudioSnapshot snapshot) ||
          snapshot.EndSampleIndex ==
            lastWaterfallSampleIndex)
        return;

      try
      {
        CwAudioSpectrumFrame frame =
          WaterfallAnalyzer.Analyze(
            snapshot);
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
          in latestTracks)
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

      TxTextBox.ReadOnly =
        state.Sending;

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
            ? $" · {status.Wpm:F1} WPM"
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
          ? $" · {ready.Mode} · BK-IN {ready.BreakIn} · {ready.Wpm:F1} WPM"
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
      EventArgs e)
    {
      CwTransmitController? tx =
        ctx.CwTransmit;
      if (tx == null)
        return;

      string text =
        TxTextBox.Text;

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
      }
      finally
      {
        updatingSourceUi = false;
      }

      RefreshUi();
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
