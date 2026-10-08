using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public sealed class IcomLanSpectrumPanel : DockContent
  {
    private readonly Context ctx;

    private static readonly long[] ScopeSpanValues =
    [
      2_500,
      5_000,
      10_000,
      25_000,
      50_000,
      100_000,
      250_000,
      500_000
    ];

    private readonly ComboBox ScopeBandBox = new();
    private readonly ComboBox ScopeModeBox = new();
    private readonly ComboBox SpanEdgeBox = new();
    private readonly Button FixedEdgeEditBtn = new();
    private readonly NumericUpDown ReferenceBox = new();
    private readonly ComboBox SweepSpeedBox = new();
    private readonly ComboBox VbwBox = new();
    private readonly Label GeometryLabel = new();
    private readonly Button StartStopBtn = new();
    private readonly Button ClearBtn = new();
    private readonly Button HoldBtn = new();
    private readonly Button PeakBtn = new();
    private readonly Button WaterfallBtn = new();
    private readonly Button ReadbackBtn = new();
    private readonly ComboBox AverageBox = new();
    private readonly ComboBox SmoothBox = new();
    private readonly ComboBox ZoomBox = new();
    private readonly Button SettingsBtn = new();
    private readonly Label StatusLabel = new();
    private readonly Label StatsLabel = new();
    private readonly IcomLanSpectrumView SpectrumView = new();
    private readonly System.Windows.Forms.Timer UiTimer = new() { Interval = 500 };
    private readonly IcomScopeState ScopeState = new();
    private readonly IcomScopeController ScopeController;

    private IcomLanSpectrumCapture? Capture;
    private IcomLanSpectrumCapture? NativeLanAssistCapture;
    private long LastScopeFrames;
    private long LastScopeUpdates;
    private DateTime LastRateTime = DateTime.UtcNow;
    private double ScopeFps;
    private double DisplayFps;
    private long LastRenderedScopeFrameTicks;
    private bool LastStatsUsedNativeLan;
    private bool LocalHold;
    private bool PeakHold;
    private bool UpdatingScopeControlUi;
    private bool SpanEdgeShowsSpan = true;
    private int PendingEdgeSyncScope = -1;
    private bool ScopeReadbackCompletedForSession;
    private bool ScopeReadbackRequestedForSession;
    private bool PendingControlSettingsApply;
    private string LastDiagnosticsText = "Spectrum diagnostics are not available while capture is stopped.";
    private IcomScopeReadbackState? LastScopeReadback;
    private DateTime? LastScopeReadbackUtc;
    private CatControlEngine? LastScopeControlBackend;

    public IcomLanSpectrumPanel(Context ctx)
    {
      this.ctx = ctx;
      ScopeController =
        new IcomScopeController(
          () => ctx.CatControl.RequestIcomScopeOutput(),
          request =>
            ctx.CatControl.RequestIcomScopeControl(
              request));

      ctx.CatControl.IcomScopeReadbackReceived +=
        CatControl_IcomScopeReadbackReceived;
      ctx.CatControl.IcomFixedEdgeReadbackReceived +=
        CatControl_IcomFixedEdgeReadbackReceived;

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

      UiTimer.Tick += (_, _) =>
      {
        RefreshTuningOverlay();
        RefreshScopeGeometryUi();
        RefreshUiStatus();
      };
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
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

      var toolbar = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = true,
        Margin = new Padding(0, 0, 0, 6)
      };

      ScopeBandBox.DropDownStyle = ComboBoxStyle.DropDownList;
      ScopeBandBox.Width = 84;
      ScopeBandBox.Items.AddRange(new object[] { "AUTO", "MAIN", "SUB" });
      ScopeBandBox.Margin = new Padding(0, 3, 8, 3);
      ScopeBandBox.SelectedIndexChanged +=
        (_, _) =>
        {
          IcomLanScopeBand selected =
            (IcomLanScopeBand)Math.Clamp(
              ScopeBandBox.SelectedIndex,
              0,
              2);
          IcomLanScopeBand previous =
            ScopeState.SelectedBand;

          if (selected !=
                IcomLanScopeBand.Auto &&
              selected != previous)
            ScopeState.Clear();

          ScopeState.SelectedBand =
            selected;
          RefreshSelectedScopeSnapshot();

          if (!UpdatingScopeControlUi)
          {
            SaveUiSettings();

            if (selected !=
                  IcomLanScopeBand.Auto &&
                CanUseSkyCatScopeControl())
            {
              byte scope =
                selected ==
                  IcomLanScopeBand.Sub
                  ? (byte)1
                  : (byte)0;

              if (SendScopeControl(
                    IcomScopeControlRequest
                      .ForSelectedScope(
                        scope),
                    $"selected {(scope == 1 ? "SUB" : "MAIN")} scope"))
                PendingEdgeSyncScope =
                  scope;
            }
          }

          RefreshScopeGeometryUi();
          UpdateScopeControlAvailability();
        };
      toolbar.Controls.Add(ScopeBandBox);

      ScopeModeBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      ScopeModeBox.Width = 92;
      ScopeModeBox.Items.AddRange(
        new object[]
        {
          "CENTER",
          "FIXED",
          "SCROLL-C",
          "SCROLL-F"
        });
      ScopeModeBox.Margin =
        new Padding(0, 3, 6, 3);
      ScopeModeBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              ScopeModeBox.SelectedIndex < 0)
            return;

          IcomScopeMode mode =
            (IcomScopeMode)
            ScopeModeBox.SelectedIndex;

          ConfigureSpanEdgeControl(
            mode is IcomScopeMode.Center or
              IcomScopeMode.ScrollCenter);
          UpdateScopeControlAvailability();

          if (!TryGetControlScope(
                out byte scope))
            return;

          bool routed =
            SendScopeControl(
              IcomScopeControlRequest.ForMode(
                scope,
                mode),
              $"mode {ScopeModeBox.SelectedItem}");

          if (routed &&
              mode is
                IcomScopeMode.Fixed or
                IcomScopeMode.ScrollFixed)
          {
            int edge =
              Math.Clamp(
                ctx.Settings.IcomLanSpectrum
                  .ScopeEdgeNumber,
                1,
                4);

            SendScopeControl(
              IcomScopeControlRequest.ForEdge(
                scope,
                edge),
              $"edge {edge}");
          }
        };
      toolbar.Controls.Add(ScopeModeBox);

      SpanEdgeBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SpanEdgeBox.Width = 86;
      SpanEdgeBox.Margin =
        new Padding(0, 3, 6, 3);
      SpanEdgeBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              SpanEdgeBox.SelectedIndex < 0 ||
              !TryGetControlScope(
                out byte scope))
            return;

          IcomLanSpectrumSettings settings =
            ctx.Settings.IcomLanSpectrum;

          if (SpanEdgeShowsSpan)
          {
            long span =
              ScopeSpanValues[
                SpanEdgeBox.SelectedIndex];

            SendScopeControl(
              IcomScopeControlRequest.ForSpan(
                scope,
                span),
              $"span {FormatHalfSpan(span)}");
          }
          else
          {
            int edge =
              SpanEdgeBox.SelectedIndex + 1;

            settings.ScopeEdgeNumber =
              edge;
            ctx.Settings.SaveToFile();

            SendScopeControl(
              IcomScopeControlRequest.ForEdge(
                scope,
                edge),
              $"edge {edge}");
          }
        };
      toolbar.Controls.Add(SpanEdgeBox);

      FixedEdgeEditBtn.Text = "EDGE…";
      FixedEdgeEditBtn.AutoSize = true;
      FixedEdgeEditBtn.Visible = false;
      FixedEdgeEditBtn.Margin =
        new Padding(0, 2, 6, 2);
      FixedEdgeEditBtn.Click +=
        (_, _) => EditSelectedFixedEdge();

      var fixedEdgeMenu =
        new ContextMenuStrip();
      fixedEdgeMenu.Items.Add(
        "Read selected Edge preset from IC-9700",
        null,
        (_, _) => RequestSelectedFixedEdgeReadback());
      FixedEdgeEditBtn.ContextMenuStrip =
        fixedEdgeMenu;

      toolbar.Controls.Add(
        FixedEdgeEditBtn);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "REF:",
          Margin =
            new Padding(0, 7, 3, 0)
        });

      ReferenceBox.Minimum = -20;
      ReferenceBox.Maximum = 20;
      ReferenceBox.DecimalPlaces = 1;
      ReferenceBox.Increment = 0.5m;
      ReferenceBox.Width = 62;
      ReferenceBox.Margin =
        new Padding(0, 3, 6, 3);
      ReferenceBox.ValueChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              !ReferenceBox.Focused)
            return;

          double referenceDb =
            (double)ReferenceBox.Value;

          ctx.Settings.IcomLanSpectrum
            .ScopeReferenceLevelDb =
            referenceDb;
          ctx.Settings.SaveToFile();

          SendScopeControlToBothReceivers(
            scope =>
              IcomScopeControlRequest
                .ForReferenceLevel(
                  scope,
                  referenceDb),
            $"reference {referenceDb:+0.0;-0.0;0.0} dB");
        };
      toolbar.Controls.Add(ReferenceBox);

      SweepSpeedBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SweepSpeedBox.Width = 70;
      SweepSpeedBox.Items.AddRange(
        new object[]
        {
          "FAST",
          "MID",
          "SLOW"
        });
      SweepSpeedBox.Margin =
        new Padding(0, 3, 8, 3);
      SweepSpeedBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              SweepSpeedBox.SelectedIndex < 0)
            return;

          IcomScopeSweepSpeed speed =
            (IcomScopeSweepSpeed)
            SweepSpeedBox.SelectedIndex;

          ctx.Settings.IcomLanSpectrum
            .ScopeSweepSpeed =
            speed;
          ctx.Settings.SaveToFile();

          SendScopeControlToBothReceivers(
            scope =>
              IcomScopeControlRequest
                .ForSweepSpeed(
                  scope,
                  speed),
            $"speed {SweepSpeedBox.SelectedItem}");
        };
      toolbar.Controls.Add(SweepSpeedBox);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "VBW:",
          Margin = new Padding(0, 7, 3, 0)
        });

      VbwBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      VbwBox.Width = 72;
      VbwBox.Items.AddRange(
        new object[]
        {
          "NARROW",
          "WIDE"
        });
      VbwBox.Margin =
        new Padding(0, 3, 8, 3);
      VbwBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              VbwBox.SelectedIndex < 0)
            return;

          IcomScopeVbw vbw =
            (IcomScopeVbw)VbwBox.SelectedIndex;

          ctx.Settings.IcomLanSpectrum
            .ScopeVbw =
            vbw;
          ctx.Settings.IcomLanSpectrum
            .ManageAdvancedScopeControls =
            true;
          ctx.Settings.SaveToFile();

          SendScopeControlToBothReceivers(
            scope =>
              IcomScopeControlRequest
                .ForVbw(
                  scope,
                  vbw),
            $"VBW {VbwBox.SelectedItem}");
        };
      toolbar.Controls.Add(VbwBox);

      GeometryLabel.AutoSize = true;
      GeometryLabel.Text = "Waiting for scope";
      GeometryLabel.ForeColor = SystemColors.GrayText;
      GeometryLabel.Margin = new Padding(0, 7, 12, 0);
      toolbar.Controls.Add(GeometryLabel);

      StartStopBtn.Text = "Start";
      StartStopBtn.AutoSize = true;
      StartStopBtn.Margin = new Padding(0, 2, 6, 2);
      StartStopBtn.Click += (_, _) =>
      {
        if (Capture == null)
          StartCapture();
        else
          StopCapture();
      };
      toolbar.Controls.Add(StartStopBtn);

      ClearBtn.Text = "Clear";
      ClearBtn.AutoSize = true;
      ClearBtn.Margin = new Padding(0, 2, 6, 2);
      ClearBtn.Click += (_, _) =>
      {
        ScopeState.Clear();
        SpectrumView.Clear();
        RefreshScopeGeometryUi();
      };
      toolbar.Controls.Add(ClearBtn);

      HoldBtn.Text = "HOLD";
      HoldBtn.AutoSize = true;
      HoldBtn.Margin = new Padding(0, 2, 6, 2);
      HoldBtn.Click += (_, _) =>
      {
        LocalHold = !LocalHold;
        SpectrumView.SetHold(LocalHold);
        UpdateDisplayButtons();
        UpdateScopeControlAvailability();

        if (!LocalHold)
        {
          RequestScopeOutputIfDue(
            force: true);

          if (PendingControlSettingsApply)
          {
            PendingControlSettingsApply =
              false;
            ApplyControlSettings();
          }
          else
          {
            RequestInitialScopeReadbackIfNeeded();
          }
        }
      };
      toolbar.Controls.Add(HoldBtn);

      PeakBtn.Text = "PEAK";
      PeakBtn.AutoSize = true;
      PeakBtn.Margin = new Padding(0, 2, 6, 2);
      PeakBtn.Click += (_, _) =>
      {
        PeakHold = !PeakHold;
        SpectrumView.SetPeakHold(PeakHold);
        UpdateDisplayButtons();
      };

      var peakMenu = new ContextMenuStrip();
      peakMenu.Items.Add(
        "Clear peak hold",
        null,
        (_, _) => SpectrumView.ClearPeakHold());
      PeakBtn.ContextMenuStrip = peakMenu;
      toolbar.Controls.Add(PeakBtn);

      WaterfallBtn.Text = "WF";
      WaterfallBtn.AutoSize = true;
      WaterfallBtn.Margin = new Padding(0, 2, 6, 2);
      WaterfallBtn.Click += (_, _) =>
      {
        IcomLanSpectrumSettings settings =
          ctx.Settings.IcomLanSpectrum;
        settings.ShowWaterfall =
          !settings.ShowWaterfall;
        SpectrumView.SetWaterfallVisible(
          settings.ShowWaterfall);
        ctx.Settings.SaveToFile();
        UpdateDisplayButtons();
      };
      toolbar.Controls.Add(WaterfallBtn);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "AVG:",
          Margin = new Padding(2, 7, 3, 0)
        });

      AverageBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      AverageBox.Width = 58;
      AverageBox.Items.AddRange(
        new object[]
        {
          "OFF",
          "2",
          "4",
          "8"
        });
      AverageBox.Margin =
        new Padding(0, 3, 6, 3);
      AverageBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              AverageBox.SelectedIndex < 0)
            return;

          int sweeps =
            AverageBox.SelectedIndex switch
            {
              0 => 1,
              1 => 2,
              2 => 4,
              _ => 8
            };

          ctx.Settings.IcomLanSpectrum
            .SpectrumAverageSweeps =
            sweeps;
          SpectrumView.SetAverageSweeps(
            sweeps);
          ctx.Settings.SaveToFile();
        };
      toolbar.Controls.Add(AverageBox);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "SMOOTH:",
          Margin = new Padding(2, 7, 3, 0)
        });

      SmoothBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      SmoothBox.Width = 58;
      SmoothBox.Items.AddRange(
        new object[]
        {
          "OFF",
          "3",
          "5",
          "9"
        });
      SmoothBox.Margin =
        new Padding(0, 3, 6, 3);
      SmoothBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              SmoothBox.SelectedIndex < 0)
            return;

          int bins =
            SmoothBox.SelectedIndex switch
            {
              0 => 1,
              1 => 3,
              2 => 5,
              _ => 9
            };

          ctx.Settings.IcomLanSpectrum
            .SpectrumSmoothingBins =
            bins;
          SpectrumView.SetSmoothingBins(
            bins);
          ctx.Settings.SaveToFile();
        };
      toolbar.Controls.Add(SmoothBox);

      toolbar.Controls.Add(
        new Label
        {
          AutoSize = true,
          Text = "ZOOM:",
          Margin = new Padding(2, 7, 3, 0)
        });

      ZoomBox.DropDownStyle =
        ComboBoxStyle.DropDownList;
      ZoomBox.Width = 58;
      ZoomBox.Items.AddRange(
        new object[]
        {
          "1×",
          "2×",
          "4×",
          "8×",
          "16×"
        });
      ZoomBox.Margin =
        new Padding(0, 3, 6, 3);
      ZoomBox.SelectedIndexChanged +=
        (_, _) =>
        {
          if (UpdatingScopeControlUi ||
              ZoomBox.SelectedIndex < 0)
            return;

          int factor =
            1 << ZoomBox.SelectedIndex;

          SpectrumView.SetZoomFactor(
            factor);
        };
      toolbar.Controls.Add(ZoomBox);

      ReadbackBtn.Text = "SYNC";
      ReadbackBtn.AutoSize = true;
      ReadbackBtn.Margin =
        new Padding(0, 2, 6, 2);
      ReadbackBtn.Click +=
        (_, _) => RequestScopeReadback();
      toolbar.Controls.Add(
        ReadbackBtn);

      SettingsBtn.Text = "Settings…";
      SettingsBtn.AutoSize = true;
      SettingsBtn.Margin = new Padding(0, 2, 0, 2);
      SettingsBtn.Click += (_, _) =>
        new SettingsDialog(
          ctx,
          "SkyRoof.Settings.IcomLanSpectrum")
          .ShowDialog(this);
      toolbar.Controls.Add(SettingsBtn);

      root.Controls.Add(toolbar, 0, 0);

      SpectrumView.Dock = DockStyle.Fill;
      SpectrumView.Margin = new Padding(0);
      SpectrumView.SpectrumPercentChanged += percent =>
      {
        ctx.Settings.IcomLanSpectrum.SpectrumHeightPercent =
          percent;
        ctx.Settings.SaveToFile();
      };

      SpectrumView.ZoomChanged += factor =>
      {
        int index =
          factor switch
          {
            1 => 0,
            2 => 1,
            4 => 2,
            8 => 3,
            _ => 4
          };

        if (ZoomBox.SelectedIndex ==
            index)
          return;

        UpdatingScopeControlUi = true;
        try
        {
          ZoomBox.SelectedIndex =
            index;
        }
        finally
        {
          UpdatingScopeControlUi = false;
        }
      };

      SpectrumView.TuneFrequencyRequested +=
        (frequencyHz, useRit) =>
        {
          if (!ctx.FrequencyControl.RadioLink.HasDownlink)
            return;

          bool tuned =
            ctx.FrequencyControl
              .TuneDownlinkToFrequency(
                frequencyHz,
                useRit,
                lightweightUi: true);

          if (!tuned)
            StatusLabel.Text =
              "Spectrum tune ignored: the target is not writable in the current RX model " +
              "(for example, manual correction is disabled or the target leaves the active CAT transverter band).";
        };

      SpectrumView.TuningCompleted +=
        () =>
        {
          ctx.FrequencyControl
            .CompleteDownlinkTuning();
          RefreshTuningOverlay();
        };

      root.Controls.Add(SpectrumView, 0, 1);

      StatusLabel.Dock = DockStyle.Fill;
      StatusLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatusLabel.ForeColor = SystemColors.GrayText;
      StatusLabel.Text =
        "Stopped. Configure the spectrum source in Settings, then start capture.";
      root.Controls.Add(StatusLabel, 0, 2);

      StatsLabel.Dock = DockStyle.Fill;
      StatsLabel.TextAlign = ContentAlignment.MiddleLeft;
      StatsLabel.ForeColor = SystemColors.GrayText;
      StatsLabel.Text = "Packets 0 · CI-V 0 · Scope 0 · 0.0 fps";
      root.Controls.Add(StatsLabel, 0, 3);

      Controls.Add(root);
    }

    private void LoadSettingsToUi()
    {
      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      UpdatingScopeControlUi = true;
      try
      {
        ScopeBandBox.SelectedIndex =
          Math.Clamp(
            (int)settings.ScopeBand,
            0,
            2);

        ScopeState.SelectedBand =
          (IcomLanScopeBand)
          ScopeBandBox.SelectedIndex;

        SpectrumView.SetHistoryRows(
          settings.WaterfallRows);
        SpectrumView.SetSpectrumPercent(
          settings.SpectrumHeightPercent);
        SpectrumView.SetWaterfallVisible(
          settings.ShowWaterfall);
        SpectrumView.SetWaterfallDisplay(
          settings.WaterfallBrightness,
          settings.WaterfallContrast,
          settings.WaterfallPalette);

        int averageSweeps =
          settings.SpectrumAverageSweeps switch
          {
            <= 1 => 1,
            <= 2 => 2,
            <= 4 => 4,
            _ => 8
          };

        settings.SpectrumAverageSweeps =
          averageSweeps;
        SpectrumView.SetAverageSweeps(
          averageSweeps);
        AverageBox.SelectedIndex =
          averageSweeps switch
          {
            1 => 0,
            2 => 1,
            4 => 2,
            _ => 3
          };

        int smoothingBins =
          settings.SpectrumSmoothingBins switch
          {
            <= 1 => 1,
            <= 3 => 3,
            <= 5 => 5,
            _ => 9
          };

        settings.SpectrumSmoothingBins =
          smoothingBins;
        SpectrumView.SetSmoothingBins(
          smoothingBins);
        SmoothBox.SelectedIndex =
          smoothingBins switch
          {
            1 => 0,
            3 => 1,
            5 => 2,
            _ => 3
          };

        ZoomBox.SelectedIndex =
          SpectrumView.ZoomFactor switch
          {
            1 => 0,
            2 => 1,
            4 => 2,
            8 => 3,
            _ => 4
          };

        double referenceDb =
          NormalizeReferenceLevel(
            settings.ScopeReferenceLevelDb);

        settings.ScopeReferenceLevelDb =
          referenceDb;

        ReferenceBox.Value =
          (decimal)referenceDb;

        int speedIndex =
          Math.Clamp(
            (int)settings.ScopeSweepSpeed,
            0,
            2);

        settings.ScopeSweepSpeed =
          (IcomScopeSweepSpeed)speedIndex;

        SweepSpeedBox.SelectedIndex =
          speedIndex;

        int vbwIndex =
          Math.Clamp(
            (int)settings.ScopeVbw,
            0,
            1);
        settings.ScopeVbw =
          (IcomScopeVbw)vbwIndex;
        VbwBox.SelectedIndex =
          vbwIndex;

        ConfigureSpanEdgeControl(
          SpanEdgeShowsSpan);
      }
      finally
      {
        UpdatingScopeControlUi = false;
      }

      UpdateDisplayButtons();
      RefreshTuningOverlay();
      RefreshScopeGeometryUi();
    }

    private void SaveUiSettings()
    {
      ctx.Settings.IcomLanSpectrum.ScopeBand =
        (IcomLanScopeBand)Math.Clamp(
          ScopeBandBox.SelectedIndex,
          0,
          2);
      ctx.Settings.SaveToFile();
    }

    private void UpdateDisplayButtons()
    {
      SetToggleButtonState(
        HoldBtn,
        LocalHold);
      SetToggleButtonState(
        PeakBtn,
        PeakHold);
      SetToggleButtonState(
        WaterfallBtn,
        ctx.Settings.IcomLanSpectrum.ShowWaterfall);
    }

    private static void SetToggleButtonState(
      Button button,
      bool active)
    {
      button.UseVisualStyleBackColor =
        !active;

      button.BackColor =
        active
          ? Theme.IsDark
            ? Theme.BlueDark
            : Theme.BlueWash
          : SystemColors.Control;

      button.ForeColor =
        active
          ? Theme.IsDark
            ? Color.White
            : Theme.LightInk
          : SystemColors.ControlText;
    }

    private bool UsingSkyCatScopeSource =>
      ctx.Settings.IcomLanSpectrum.Source == IcomLanSpectrumSource.SkyCat;

    private bool UsingDirectLanSource =>
      ctx.Settings.IcomLanSpectrum.Source == IcomLanSpectrumSource.DirectLan;

    private void StartCapture()
    {
      if (Capture != null) return;

      if (LocalHold)
      {
        LocalHold = false;
        SpectrumView.SetHold(false);
        UpdateDisplayButtons();
      }

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
      ScopeReadbackCompletedForSession = false;
      ScopeReadbackRequestedForSession = false;
      PendingControlSettingsApply = false;
      LastScopeReadback = null;
      LastScopeReadbackUtc = null;
      LastScopeControlBackend = null;

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

      LastScopeControlBackend =
        ctx.CatControl
          .GetIcomScopeControlBackend();

      // Enabling scope output is required to obtain waveform data, but do
      // not push saved MODE/SPAN/EDGE/REF/SPEED/VBW values on startup. Query
      // the radio first so merely opening the spectrum cannot overwrite the
      // operator's current front-panel scope state.
      RequestScopeOutputIfDue(
        force: true);

      if (LastScopeControlBackend != null)
        RequestInitialScopeReadbackIfNeeded();
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

      // A stopped capture is a complete acquisition-session boundary. Cancel
      // unsent scope-only CAT writes, but leave ordinary RX/TX/PTT CAT state
      // untouched because the radio-control engine has a longer lifetime.
      ctx.CatControl.CancelIcomScopeRequests();

      // Do not carry receiver caches, AUTO selection, HOLD state, or controller
      // rate-limit state into a later source/session.
      ScopeController.Reset();
      PendingEdgeSyncScope = -1;
      ScopeReadbackCompletedForSession = false;
      ScopeReadbackRequestedForSession = false;
      PendingControlSettingsApply = false;
      LastScopeReadback = null;
      LastScopeReadbackUtc = null;
      LastScopeControlBackend = null;
      LastDiagnosticsText =
        "Spectrum diagnostics are not available while capture is stopped.";
      ScopeState.Clear();
      LastRenderedScopeFrameTicks = 0;

      if (LocalHold)
      {
        LocalHold = false;
        SpectrumView.SetHold(false);
      }

      SpectrumView.Clear();
      UpdateDisplayButtons();
      RefreshScopeGeometryUi();

      StartStopBtn.Text = "Start";
      StatusLabel.Text = "Spectrum capture stopped.";
    }

    private void RefreshSelectedScopeSnapshot()
    {
      if (LocalHold)
        return;

      IcomScopeFrame? selectedFrame =
        ScopeState.LatestSelectedFrame;

      SpectrumView.Clear();

      if (selectedFrame != null)
        SpectrumView.PushFrame(
          selectedFrame);

      RefreshScopeGeometryUi();
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

      // HOLD is a local display freeze. Keep consuming capture events so the
      // transport stays healthy, but freeze the displayed scope state/geometry
      // together with the trace and waterfall until HOLD is released.
      if (LocalHold)
      {
        LastRenderedScopeFrameTicks = ticks;
        return;
      }

      ScopeState.Update(frame);
      if (!ScopeState.ShouldDisplay(frame))
        return;

      SynchronizePendingEdgeIfNeeded(
        frame);
      SpectrumView.PushFrame(frame);
      LastRenderedScopeFrameTicks = ticks;
      RefreshScopeGeometryUi();
    }

    private void SynchronizePendingEdgeIfNeeded(
      IcomScopeFrame frame)
    {
      if (PendingEdgeSyncScope != -2 &&
          PendingEdgeSyncScope !=
            frame.Scope)
        return;

      PendingEdgeSyncScope = -1;

      if (!CanUseSkyCatScopeControl() ||
          (frame.Mode !=
             (byte)IcomScopeMode.Fixed &&
           frame.Mode !=
             (byte)IcomScopeMode.ScrollFixed))
        return;

      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      ScopeController.RequestControl(
        settings.Source,
        settings.ControlPath,
        IcomScopeControlRequest.ForEdge(
          frame.Scope,
          Math.Clamp(
            settings.ScopeEdgeNumber,
            1,
            4)));
    }

    private void RefreshScopeGeometryUi()
    {
      IcomScopeFrame? frame =
        ScopeState.LatestSelectedFrame;

      if (frame == null)
      {
        UpdatingScopeControlUi = true;
        try
        {
          ScopeModeBox.SelectedIndex = -1;
          SpanEdgeBox.SelectedIndex = -1;
        }
        finally
        {
          UpdatingScopeControlUi = false;
        }

        GeometryLabel.Text =
          "Waiting for scope";
        UpdateScopeControlAvailability();
        return;
      }

      IcomScopeGeometry geometry =
        frame.Geometry;

      UpdatingScopeControlUi = true;
      try
      {
        ScopeModeBox.SelectedIndex =
          frame.Mode <=
            (byte)IcomScopeMode.ScrollFixed
            ? frame.Mode
            : -1;

        bool spanMode =
          frame.Mode is
            (byte)IcomScopeMode.Center or
            (byte)IcomScopeMode.ScrollCenter;

        ConfigureSpanEdgeControl(
          spanMode);

        if (spanMode &&
            geometry.IsValid)
        {
          int spanIndex =
            Array.IndexOf(
              ScopeSpanValues,
              geometry.SpanHz);

          if (spanIndex >= 0)
            SpanEdgeBox.SelectedIndex =
              spanIndex;
        }
        else if (!spanMode)
        {
          SpanEdgeBox.SelectedIndex =
            Math.Clamp(
              ctx.Settings.IcomLanSpectrum
                .ScopeEdgeNumber,
              1,
              4) - 1;
        }

        ReferenceBox.Value =
          (decimal)NormalizeReferenceLevel(
            ctx.Settings.IcomLanSpectrum
              .ScopeReferenceLevelDb);

        SweepSpeedBox.SelectedIndex =
          Math.Clamp(
            (int)ctx.Settings.IcomLanSpectrum
              .ScopeSweepSpeed,
            0,
            2);

        VbwBox.SelectedIndex =
          Math.Clamp(
            (int)ctx.Settings.IcomLanSpectrum
              .ScopeVbw,
            0,
            1);

      }
      finally
      {
        UpdatingScopeControlUi = false;
      }

      if (!geometry.IsValid)
      {
        GeometryLabel.Text =
          "Frequency geometry unavailable";
        UpdateScopeControlAvailability();
        return;
      }

      long displayOffsetHz =
        GetScopeDisplayFrequencyOffsetHz(
          frame.Scope);

      GeometryLabel.Text =
        frame.Mode ==
          (byte)IcomScopeMode.Center
          ? FormatHalfSpan(
              geometry.SpanHz)
          : $"{FormatToolbarFrequency(checked(geometry.LowerFrequencyHz + displayOffsetHz))} — " +
            $"{FormatToolbarFrequency(checked(geometry.UpperFrequencyHz + displayOffsetHz))}";

      UpdateScopeControlAvailability();
    }

    private void ConfigureSpanEdgeControl(
      bool showSpan)
    {
      if (SpanEdgeBox.Items.Count > 0 &&
          SpanEdgeShowsSpan == showSpan)
        return;

      SpanEdgeShowsSpan =
        showSpan;
      FixedEdgeEditBtn.Visible =
        !showSpan;

      bool previousUpdating =
        UpdatingScopeControlUi;
      UpdatingScopeControlUi = true;

      try
      {
        SpanEdgeBox.Items.Clear();

        if (showSpan)
        {
          foreach (long span in
                   ScopeSpanValues)
            SpanEdgeBox.Items.Add(
              FormatSpanChoice(span));
        }
        else
        {
          SpanEdgeBox.Items.AddRange(
            new object[]
            {
              "EDGE 1",
              "EDGE 2",
              "EDGE 3",
              "EDGE 4"
            });

          SpanEdgeBox.SelectedIndex =
            Math.Clamp(
              ctx.Settings.IcomLanSpectrum
                .ScopeEdgeNumber,
              1,
              4) - 1;
        }
      }
      finally
      {
        UpdatingScopeControlUi =
          previousUpdating;
      }
    }

    private bool TryGetControlScope(
      out byte scope)
    {
      IcomLanScopeBand selected =
        ScopeState.SelectedBand;

      if (selected ==
          IcomLanScopeBand.Main)
      {
        scope = 0;
        return true;
      }

      if (selected ==
          IcomLanScopeBand.Sub)
      {
        scope = 1;
        return true;
      }

      IcomScopeFrame? frame =
        ScopeState.LatestSelectedFrame;

      if (frame != null)
      {
        scope =
          frame.Scope == 1
            ? (byte)1
            : (byte)0;
        return true;
      }

      scope = 0;
      return false;
    }

    private bool IsSkyCatScopeControlConfigured()
    {
      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      return
        Capture != null &&
        settings.Source !=
          IcomLanSpectrumSource.DirectLan &&
        IcomScopeController.ResolveControlPath(
          settings.Source,
          settings.ControlPath) ==
          IcomScopeControlPath.SkyCat;
    }

    private bool CanUseSkyCatScopeControl() =>
      IsSkyCatScopeControlConfigured() &&
      ctx.CatControl.HasIcomScopeControlBackend;

    private void UpdateScopeControlAvailability()
    {
      bool skyCatWritable =
        CanUseSkyCatScopeControl();
      bool writeReady =
        !LocalHold &&
        skyCatWritable &&
        ScopeReadbackCompletedForSession;

      // MAIN/SUB remains usable as a local display selector in read-only mode,
      // but do not let it send SCOPE_SELECT until the initial radio readback
      // has established the current state.
      ScopeBandBox.Enabled =
        !LocalHold &&
        (!skyCatWritable ||
         ScopeReadbackCompletedForSession);

      bool enabled =
        writeReady &&
        TryGetControlScope(
          out _);

      ScopeModeBox.Enabled =
        enabled;
      SpanEdgeBox.Enabled =
        enabled &&
        ScopeModeBox.SelectedIndex >= 0;
      FixedEdgeEditBtn.Enabled =
        enabled &&
        !SpanEdgeShowsSpan &&
        ScopeState.LatestSelectedFrame != null;
      ReferenceBox.Enabled =
        enabled;
      SweepSpeedBox.Enabled =
        enabled;
      VbwBox.Enabled =
        enabled;
      ReadbackBtn.Enabled =
        !LocalHold &&
        CanUseSkyCatScopeControl();
    }

    private void RequestInitialScopeReadbackIfNeeded()
    {
      if (LocalHold ||
          ScopeReadbackCompletedForSession ||
          ScopeReadbackRequestedForSession ||
          !CanUseSkyCatScopeControl())
        return;

      RequestScopeReadback();
      UpdateScopeControlAvailability();
    }

    private void RequestScopeReadback()
    {
      if (LocalHold ||
          !CanUseSkyCatScopeControl())
        return;

      if (ctx.CatControl
          .RequestIcomScopeReadback())
      {
        ScopeReadbackRequestedForSession =
          true;
        StatusLabel.Text =
          "Reading IC-9700 scope settings through SkyCAT…";
      }
    }

    private void CatControl_IcomScopeReadbackReceived(
      IcomScopeReadbackState state)
    {
      if (IsDisposed ||
          Disposing)
        return;

      try
      {
        BeginInvoke(
          (Action)(() =>
          {
            if (!IsDisposed &&
                !LocalHold)
              ApplyScopeReadback(
                state);
          }));
      }
      catch (InvalidOperationException)
      {
        // Panel is closing.
      }
    }

    private void ApplyScopeReadback(
      IcomScopeReadbackState state)
    {
      LastScopeReadback =
        state;
      LastScopeReadbackUtc =
        DateTime.UtcNow;

      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      bool applyPendingSettings =
        PendingControlSettingsApply;

      ScopeReadbackCompletedForSession =
        true;

      byte activeScope;
      if (!TryGetControlScope(
            out activeScope))
        activeScope =
          state.SelectedScope;

      if (!applyPendingSettings &&
          activeScope == 1)
      {
        settings.ScopeEdgeNumber =
          state.SubEdge;
        settings.ScopeReferenceLevelDb =
          state.SubReferenceDb;
        settings.ScopeSweepSpeed =
          state.SubSpeed;
        settings.ScopeVbw =
          state.SubVbw;
      }
      else if (!applyPendingSettings)
      {
        settings.ScopeEdgeNumber =
          state.MainEdge;
        settings.ScopeReferenceLevelDb =
          state.MainReferenceDb;
        settings.ScopeSweepSpeed =
          state.MainSpeed;
        settings.ScopeVbw =
          state.MainVbw;
      }

      if (!applyPendingSettings)
      {
        settings.ScopeDuringTx =
          state.ScopeDuringTx;
        settings.ScopeCenterType =
          state.CenterType;
        settings.ScopeMarkerPosition =
          state.MarkerPosition;

        ctx.Settings.SaveToFile();
      }

      UpdatingScopeControlUi = true;
      try
      {
        ReferenceBox.Value =
          (decimal)NormalizeReferenceLevel(
            settings.ScopeReferenceLevelDb);
        SweepSpeedBox.SelectedIndex =
          Math.Clamp(
            (int)settings.ScopeSweepSpeed,
            0,
            2);
        VbwBox.SelectedIndex =
          Math.Clamp(
            (int)settings.ScopeVbw,
            0,
            1);

        if (!SpanEdgeShowsSpan)
          SpanEdgeBox.SelectedIndex =
            Math.Clamp(
              settings.ScopeEdgeNumber,
              1,
              4) -
            1;
      }
      finally
      {
        UpdatingScopeControlUi = false;
      }

      UpdateScopeControlAvailability();

      if (applyPendingSettings)
      {
        PendingControlSettingsApply =
          false;
        ScopeReadbackRequestedForSession =
          false;
        QueueSavedScopeControlsIfWritable();
        StatusLabel.Text =
          "IC-9700 scope state read first; applying the pending explicit spectrum-control changes.";
      }
      else
      {
        StatusLabel.Text =
          $"IC-9700 scope readback synchronized ({(activeScope == 1 ? "SUB" : "MAIN")}).";
      }
    }

    private void SendScopeControlToBothReceivers(
      Func<byte, IcomScopeControlRequest> requestFactory,
      string description)
    {
      bool main =
        SendScopeControl(
          requestFactory(0),
          $"MAIN {description}");

      bool sub =
        SendScopeControl(
          requestFactory(1),
          $"SUB {description}");

      if (main || sub)
        StatusLabel.Text =
          $"Scope control queued for MAIN/SUB: {description}.";
    }

    private bool SendScopeControl(
      IcomScopeControlRequest request,
      string description)
    {
      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      bool routed =
        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          request);

      if (routed)
        ScopeReadbackRequestedForSession =
          false;

      StatusLabel.Text =
        routed
          ? $"Scope control queued via {ScopeController.EffectivePath}: {description}."
          : "Scope control is read-only or no active SkyCAT control engine is available. " +
            "Set Scope control path to SkyCAT when using RS-BA1 waveform data.";

      return routed;
    }

    private void RequestSelectedFixedEdgeReadback()
    {
      if (LocalHold ||
          !CanUseSkyCatScopeControl())
        return;

      IcomScopeFrame? frame =
        ScopeState.LatestSelectedFrame;

      if (frame == null ||
          !frame.Geometry.IsValid)
        return;

      int frequencyRange =
        GetFixedEdgeFrequencyRange(
          frame.Geometry);
      int edgeNumber =
        Math.Clamp(
          ctx.Settings.IcomLanSpectrum
            .ScopeEdgeNumber,
          1,
          4);

      if (frequencyRange == 0)
      {
        StatusLabel.Text =
          "Cannot read the selected Edge preset because the current raw scope geometry is outside the IC-9700 144/430/1200 MHz ranges.";
        return;
      }

      if (ctx.CatControl
          .RequestIcomFixedEdgeReadback(
            frequencyRange,
            edgeNumber))
      {
        StatusLabel.Text =
          $"Reading IC-9700 fixed Edge {edgeNumber} preset…";
      }
    }

    private void CatControl_IcomFixedEdgeReadbackReceived(
      IcomFixedEdgeReadbackState state)
    {
      if (IsDisposed ||
          Disposing)
        return;

      try
      {
        BeginInvoke(
          (Action)(() =>
          {
            if (IsDisposed)
              return;

            IcomLanSpectrumSettings settings =
              ctx.Settings.IcomLanSpectrum;

            settings.FixedEdgePresets ??=
              new Dictionary<string, IcomScopeFixedEdgePreset>();

            string key =
              $"{state.FrequencyRange}:{state.EdgeNumber}";

            settings.FixedEdgePresets[key] =
              new IcomScopeFixedEdgePreset
              {
                LowerHz =
                  state.LowerHz,
                UpperHz =
                  state.UpperHz
              };

            ctx.Settings.SaveToFile();

            StatusLabel.Text =
              $"Read IC-9700 Edge {state.EdgeNumber}: " +
              $"{state.LowerHz / 1_000_000.0:0.000}–" +
              $"{state.UpperHz / 1_000_000.0:0.000} MHz.";
          }));
      }
      catch (InvalidOperationException)
      {
        // Panel is closing.
      }
    }


    private void EditSelectedFixedEdge()
    {
      if (LocalHold ||
          !CanUseSkyCatScopeControl())
        return;

      IcomScopeFrame? frame =
        ScopeState.LatestSelectedFrame;

      if (frame == null ||
          !frame.Geometry.IsValid ||
          (frame.Mode !=
             (byte)IcomScopeMode.Fixed &&
           frame.Mode !=
             (byte)IcomScopeMode.ScrollFixed))
        return;

      int frequencyRange =
        GetFixedEdgeFrequencyRange(
          frame.Geometry);

      if (frequencyRange == 0)
      {
        StatusLabel.Text =
          "Fixed-edge editing is unavailable because the raw IC-9700 scope geometry is outside the 144/430/1200 MHz preset ranges.";
        return;
      }

      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;
      int edgeNumber =
        Math.Clamp(
          settings.ScopeEdgeNumber,
          1,
          4);
      string key =
        $"{frequencyRange}:{edgeNumber}";

      (long minimum,
       long maximum) =
        GetFixedEdgeRangeBounds(
          frequencyRange);

      long lowerHz =
        Math.Clamp(
          RoundToKilohertz(
            frame.Geometry.LowerFrequencyHz),
          minimum,
          maximum -
          1_000);
      long upperHz =
        Math.Clamp(
          RoundToKilohertz(
            frame.Geometry.UpperFrequencyHz),
          lowerHz +
          1_000,
          maximum);

      settings.FixedEdgePresets ??=
        new Dictionary<string, IcomScopeFixedEdgePreset>();

      if (settings.FixedEdgePresets
          .TryGetValue(
            key,
            out IcomScopeFixedEdgePreset? saved) &&
          saved.LowerHz >= minimum &&
          saved.UpperHz <= maximum &&
          saved.UpperHz > saved.LowerHz)
      {
        lowerHz =
          saved.LowerHz;
        upperHz =
          saved.UpperHz;
      }

      using var dialog =
        new IcomScopeFixedEdgeDialog(
          frequencyRange,
          edgeNumber,
          lowerHz,
          upperHz);

      if (dialog.ShowDialog(this) !=
          DialogResult.OK)
        return;

      settings.FixedEdgePresets[key] =
        new IcomScopeFixedEdgePreset
        {
          LowerHz =
            dialog.LowerHz,
          UpperHz =
            dialog.UpperHz
        };

      ctx.Settings.SaveToFile();

      bool routed =
        SendScopeControl(
          IcomScopeControlRequest.ForFixedEdge(
            frequencyRange,
            edgeNumber,
            dialog.LowerHz,
            dialog.UpperHz),
          $"fixed edge {edgeNumber} {dialog.LowerHz / 1_000_000.0:0.000}–{dialog.UpperHz / 1_000_000.0:0.000} MHz");

      if (routed &&
          TryGetControlScope(
            out byte scope))
      {
        SendScopeControl(
          IcomScopeControlRequest.ForEdge(
            scope,
            edgeNumber),
          $"edge {edgeNumber}");
      }
    }

    private static int GetFixedEdgeFrequencyRange(
      IcomScopeGeometry geometry)
    {
      if (!geometry.IsValid)
        return 0;

      long center =
        geometry.LowerFrequencyHz +
        geometry.SpanHz / 2;

      if (center is >=
            144_000_000 and <=
            148_000_000)
        return 1;

      if (center is >=
            430_000_000 and <=
            450_000_000)
        return 2;

      if (center is >=
            1_240_000_000 and <=
            1_300_000_000)
        return 3;

      return 0;
    }

    private static (
      long Minimum,
      long Maximum)
      GetFixedEdgeRangeBounds(
        int frequencyRange) =>
      frequencyRange switch
      {
        1 =>
          (144_000_000,
           148_000_000),
        2 =>
          (430_000_000,
           450_000_000),
        3 =>
          (1_240_000_000,
           1_300_000_000),
        _ =>
          throw new ArgumentOutOfRangeException(
            nameof(frequencyRange))
      };

    private static long RoundToKilohertz(
      long frequencyHz) =>
      checked(
        1_000 *
        (long)Math.Round(
          frequencyHz /
          1_000.0,
          MidpointRounding.AwayFromZero));

    private static double NormalizeReferenceLevel(
      double referenceDb)
    {
      if (!double.IsFinite(
            referenceDb))
        return 0;

      return Math.Clamp(
        Math.Round(
          referenceDb * 2,
          MidpointRounding.AwayFromZero) / 2.0,
        -20.0,
        20.0);
    }

    private static string FormatSpanChoice(
      long spanHz)
    {
      return spanHz >= 1_000_000
        ? $"{spanHz / 1_000_000.0:0.###} MHz"
        : $"{spanHz / 1_000.0:0.###} kHz";
    }

    private void RefreshTuningOverlay()
    {
      RadioLink link =
        ctx.FrequencyControl.RadioLink;

      long receiveHz =
        double.IsFinite(
          link.CorrectedDownlinkFrequency) &&
        link.CorrectedDownlinkFrequency > 0
          ? checked((long)Math.Round(
              link.CorrectedDownlinkFrequency))
          : 0;

      long transmitHz =
        double.IsFinite(
          link.CorrectedUplinkFrequency) &&
        link.CorrectedUplinkFrequency > 0
          ? checked((long)Math.Round(
              link.CorrectedUplinkFrequency))
          : 0;

      Slicer.Mode? receiveMode = null;
      Slicer.Mode? transmitMode = null;

      if (link.TxCust != null)
      {
        if (link.HasDownlink)
          receiveMode =
            link.DownlinkMode;

        if (link.HasUplink)
          transmitMode =
            link.UplinkMode;
      }
      else if (ctx.Slicer != null)
      {
        receiveMode =
          ctx.Slicer.CurrentMode;
      }

      SpectrumView.SetTuningOverlay(
        receiveHz,
        receiveMode,
        transmitHz,
        transmitMode,
        GetScopeDisplayFrequencyOffsetHz(
          0),
        GetScopeDisplayFrequencyOffsetHz(
          1));
    }

    private long GetScopeDisplayFrequencyOffsetHz(
      byte scope)
    {
      RadioLink link =
        ctx.FrequencyControl.RadioLink;
      TransverterSettings settings =
        ctx.Settings.Transverter;

      if (scope == 1)
      {
        if (!settings.TxCatOffsetEnabled ||
            !double.IsFinite(
              link.CorrectedUplinkFrequency) ||
            link.CorrectedUplinkFrequency <= 0)
          return 0;

        return settings.GetCatLoOffset(
          link.CorrectedUplinkFrequency);
      }

      if (!settings.RxCatOffsetEnabled ||
          !double.IsFinite(
            link.CorrectedDownlinkFrequency) ||
          link.CorrectedDownlinkFrequency <= 0)
        return 0;

      return settings.GetCatLoOffset(
        link.CorrectedDownlinkFrequency);
    }

    private static string FormatHalfSpan(long spanHz)
    {
      if (spanHz <= 0)
        return "Span unknown";

      double half = spanHz / 2.0;

      return half >= 1_000_000
        ? $"±{half / 1_000_000.0:0.###} MHz"
        : $"±{half / 1_000.0:0.###} kHz";
    }

    private static string FormatToolbarFrequency(
      long frequencyHz)
    {
      return frequencyHz > 0
        ? $"{frequencyHz / 1_000_000.0:0.000000} MHz"
        : "—";
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
      if (capture == null)
        return;

      CatControlEngine? scopeBackend =
        ctx.CatControl
          .GetIcomScopeControlBackend();

      if (!ReferenceEquals(
            scopeBackend,
            LastScopeControlBackend))
      {
        LastScopeControlBackend =
          scopeBackend;
        ScopeReadbackCompletedForSession =
          false;
        ScopeReadbackRequestedForSession =
          false;
        LastScopeReadback = null;
        LastScopeReadbackUtc = null;
        PendingEdgeSyncScope = -1;
        ScopeController.Reset();
      }

      if (!LocalHold &&
          !ScopeReadbackCompletedForSession &&
          IsSkyCatScopeControlConfigured() &&
          scopeBackend != null)
      {
        RequestScopeOutputIfDue(
          force: true);
        RequestInitialScopeReadbackIfNeeded();
      }

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

      IcomLanSpectrumSettings spectrumSettings =
        ctx.Settings.IcomLanSpectrum;

      IcomScopeControlPath resolvedControlPath =
        IcomScopeController.ResolveControlPath(
          spectrumSettings.Source,
          spectrumSettings.ControlPath);

      (int Pending, long Dropped, long Rejected)? queueStats =
        ctx.CatControl.GetIcomScopeControlQueueStats();

      if (!LocalHold &&
          ScopeReadbackCompletedForSession &&
          !ScopeReadbackRequestedForSession &&
          queueStats.HasValue &&
          queueStats.Value.Pending == 0 &&
          CanUseSkyCatScopeControl() &&
          ctx.CatControl.RequestIcomScopeReadback())
      {
        ScopeReadbackRequestedForSession =
          true;
      }

      string controlQueue =
        queueStats.HasValue
          ? $" · Q {queueStats.Value.Pending:N0}" +
            $" / Drop {queueStats.Value.Dropped:N0}" +
            $" / Reject {queueStats.Value.Rejected:N0}"
          : "";

      StatsLabel.Text =
        $"Data {FormatSpectrumSource(spectrumSettings.Source)} · " +
        $"Transport {radio} · Ctrl {FormatControlPath(resolvedControlPath)}" +
        controlQueue +
        (LastScopeReadbackUtc.HasValue
          ? $" · RB {LastScopeReadbackUtc.Value:HH:mm:ss}Z"
          : "") +
        $" · Frames {effectiveCapture.PacketCount:N0} · " +
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
                  (CanUseSkyCatScopeControl()
                    ? "CI-V 27 10 / 27 11 are being reasserted."
                    : "scope output control is read-only.")
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
                $"{nativeLan.DetectedCivPort?.ToString() ?? "auto"} · " +
                (CanUseSkyCatScopeControl()
                  ? "SkyCAT controls scope."
                  : "scope control read-only.")
              : $"Receiving IC-9700 CI-V 27 00 spectrum data · {capture.TransportName}.";
      }
    }

    private static string FormatSpectrumSource(
      IcomLanSpectrumSource source) =>
      source switch
      {
        IcomLanSpectrumSource.SkyCat => "SkyCAT",
        IcomLanSpectrumSource.RsBa1 => "RS-BA1",
        IcomLanSpectrumSource.DirectLan => "Direct LAN",
        _ => source.ToString()
      };

    private static string FormatControlPath(
      IcomScopeControlPath path) =>
      path switch
      {
        IcomScopeControlPath.SkyCat => "SkyCAT",
        IcomScopeControlPath.DirectLan => "Direct LAN (experimental)",
        IcomScopeControlPath.ReadOnly => "Read only",
        _ => path.ToString()
      };

    private void RequestScopeOutputIfDue(bool force)
    {
      if (UsingDirectLanSource)
        return;

      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      ScopeController.RequestOutputIfDue(
        settings.Source,
        settings.ControlPath,
        force);
    }

    private void QueueSavedScopeControlsIfWritable()
    {
      if (!CanUseSkyCatScopeControl())
        return;

      IcomLanSpectrumSettings settings =
        ctx.Settings.IcomLanSpectrum;

      if (settings.ScopeBand !=
          IcomLanScopeBand.Auto)
      {
        byte selectedScope =
          settings.ScopeBand ==
            IcomLanScopeBand.Sub
            ? (byte)1
            : (byte)0;

        if (ScopeController.RequestControl(
              settings.Source,
              settings.ControlPath,
              IcomScopeControlRequest
                .ForSelectedScope(
                  selectedScope)))
          PendingEdgeSyncScope =
            selectedScope;
      }

      IcomScopeSweepSpeed speed =
        (IcomScopeSweepSpeed)Math.Clamp(
          (int)settings.ScopeSweepSpeed,
          0,
          2);

      // Sweep speed is per receiver. Keep both receivers coherent so changing
      // MAIN/SUB later does not resurrect an old front-panel speed.
      ScopeController.RequestControl(
        settings.Source,
        settings.ControlPath,
        IcomScopeControlRequest.ForSweepSpeed(
          0,
          speed));

      ScopeController.RequestControl(
        settings.Source,
        settings.ControlPath,
        IcomScopeControlRequest.ForSweepSpeed(
          1,
          speed));

      IcomScopeVbw vbw =
        (IcomScopeVbw)Math.Clamp(
          (int)settings.ScopeVbw,
          0,
          1);

      if (settings.ManageAdvancedScopeControls)
      {
        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForVbw(
            0,
            vbw));

        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForVbw(
            1,
            vbw));

        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForScopeDuringTx(
            settings.ScopeDuringTx));

        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForCenterType(
            settings.ScopeCenterType));

        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForMarkerPosition(
            settings.ScopeMarkerPosition));
      }

      double referenceDb =
        NormalizeReferenceLevel(
          settings.ScopeReferenceLevelDb);

      ScopeController.RequestControl(
        settings.Source,
        settings.ControlPath,
        IcomScopeControlRequest
          .ForReferenceLevel(
            0,
            referenceDb));

      ScopeController.RequestControl(
        settings.Source,
        settings.ControlPath,
        IcomScopeControlRequest
          .ForReferenceLevel(
            1,
            referenceDb));

      if (!TryGetControlScope(
            out byte scope))
      {
        // AUTO has no selected receiver until the first displayable frame.
        // SPEED/REF are already synchronized globally; defer only the
        // receiver-specific fixed-edge write to whichever scope appears first.
        PendingEdgeSyncScope = -2;
        return;
      }

      IcomScopeFrame? frame =
        ScopeState.LatestSelectedFrame;

      if (frame?.Mode is
            (byte)IcomScopeMode.Fixed or
            (byte)IcomScopeMode.ScrollFixed)
      {
        ScopeController.RequestControl(
          settings.Source,
          settings.ControlPath,
          IcomScopeControlRequest.ForEdge(
            scope,
            Math.Clamp(
              settings.ScopeEdgeNumber,
              1,
              4)));
        PendingEdgeSyncScope = -1;
      }
      else if (frame == null)
      {
        PendingEdgeSyncScope = scope;
      }

    }

    internal void ApplyDisplaySettings()
    {
      LoadSettingsToUi();
    }

    internal void ApplyControlSettings()
    {
      ScopeReadbackRequestedForSession =
        false;

      // HOLD is an atomic display snapshot. Remember an explicit Settings edit
      // and apply it only after the snapshot is released.
      if (LocalHold)
      {
        PendingControlSettingsApply =
          true;
        return;
      }

      LoadSettingsToUi();

      if (Capture == null)
        return;

      if (CanUseSkyCatScopeControl() &&
          !ScopeReadbackCompletedForSession)
      {
        // The operator explicitly changed Settings before initialization
        // finished. Read the radio first, then apply those deliberate edits.
        PendingControlSettingsApply =
          true;
        RequestInitialScopeReadbackIfNeeded();
        StatusLabel.Text =
          "Waiting for the initial IC-9700 scope readback before applying spectrum-control changes.";
        return;
      }

      QueueSavedScopeControlsIfWritable();
    }

    internal void ApplySettings()
    {
      // Changing transport/source is a new acquisition session, so a stale
      // display HOLD must not survive the restart.
      if (LocalHold)
      {
        LocalHold = false;
        SpectrumView.SetHold(false);
        UpdateDisplayButtons();
      }

      bool restart = Capture != null;

      if (restart)
        StopCapture();

      LoadSettingsToUi();

      if (restart)
        StartCapture();
    }

    private void IcomLanSpectrumPanel_FormClosing(object? sender, FormClosingEventArgs e)
    {
      ctx.CatControl.IcomScopeReadbackReceived -=
        CatControl_IcomScopeReadbackReceived;
      ctx.CatControl.IcomFixedEdgeReadbackReceived -=
        CatControl_IcomFixedEdgeReadbackReceived;
      UiTimer.Stop();
      StopCapture();

      ctx.IcomLanSpectrumPanel = null;
      ctx.MainForm.IcomLanSpectrumMNU.Checked = false;
    }
  }
}
