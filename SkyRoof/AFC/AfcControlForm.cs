using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof.AFC
{
  /// <summary>
  /// AFC operator console. Closing the visible window while RX AFC is
  /// checked hides it; the IF capture/servo stays owned by this form until
  /// the RX AFC switch is cleared or the application exits.
  /// </summary>
  public sealed class AfcControlForm : Form
  {
    private readonly Context ctx;
    private readonly FrequencyWidget owner;
    private readonly AfcIfInput input = new();
    private readonly AfcPolarityCalibrator polarityProbe = new();
    private readonly AfcThemePalette palette = AfcThemePalette.Current;
    private readonly PropertyGrid settingsGrid = new();
    private readonly AfcSpectrumView spectrum = new();
    private readonly ComboBox spanBox = new();
    private readonly ComboBox averageBox = new();
    private readonly CheckBox waterfallCheck = new();
    private readonly CheckBox peakCheck = new();
    private readonly CheckBox haloCheck = new();
    private readonly CheckBox bhCheck = new();
    private DateTime displayUtc;
    private double inputRms = double.NaN, clipFraction;
    private float noiseFloorDb;
    private readonly TabControl tabs = new();
    private readonly Label heading = new();
    private readonly Label inputStatus = new();
    private readonly Label stateLabel = new();
    private readonly Label stateDetail = new();
    private readonly Label[] metrics = { new(), new(), new() };
    private readonly Label[] metricCaptions = { new(), new(), new() };
    private readonly Label[] metricHelp = { new(), new(), new() };
    private readonly Label levelCaption = new();
    private UiLanguage renderedLanguage = (UiLanguage)(-1);
    private readonly Panel[] metricCards = { new(), new(), new() };
    private readonly Button startButton = new();
    private readonly Button offButton = new();
    private readonly Button assistButton = new();
    private readonly Button autoButton = new();
    private readonly Button lockButton = new();
    private readonly Button centerButton = new();
    private readonly Label controlHint = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private AfcSignalMode currentMode;
    private bool active;

    internal bool CaptureRunning => active && input.IsRunning;

    public AfcControlForm(Context ctx, FrequencyWidget owner)
    {
      this.ctx = ctx;
      this.owner = owner;
      SettingsLanguage.Set(ctx.Settings.Ui.Language);
      currentMode = owner.CurrentAfcMode;
      Text = "SkyRoof - FM / CW / SSB AFC";
      Font = new Font("Segoe UI", 9.5f);
      AutoScaleMode = AutoScaleMode.Dpi;
      StartPosition = FormStartPosition.CenterParent;
      MinimumSize = new Size(720, 570);
      Size = new Size(900, 725);
      BackColor = palette.Background;
      ForeColor = palette.Text;

      tabs.Dock = DockStyle.Fill;
      tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
      tabs.ItemSize = new Size(110, 29);
      tabs.SizeMode = TabSizeMode.Fixed;
      tabs.DrawItem += (_, e) =>
      {
        var page = tabs.TabPages[e.Index];
        bool selected = e.Index == tabs.SelectedIndex;
        using var bg = new SolidBrush(selected ? palette.Surface : palette.Background);
        using var fg = new SolidBrush(selected ? palette.Accent : palette.Muted);
        e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, page.Text, Font, e.Bounds,
          selected ? palette.Accent : palette.Muted,
          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected)
          using (var line = new Pen(palette.Accent, 2))
            e.Graphics.DrawLine(line, e.Bounds.Left + 3,
              e.Bounds.Bottom - 2, e.Bounds.Right - 3, e.Bounds.Bottom - 2);
      };

      var trackingPage = new TabPage("实时追踪")
        { BackColor = palette.Background, ForeColor = palette.Text };
      var advancedPage = new TabPage("高级设置")
        { BackColor = palette.Background, ForeColor = palette.Text };

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
        Padding = new Padding(12, 12, 12, 12),
        BackColor = palette.Background
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 119));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));

      var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
      header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
      header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
      heading.Dock = DockStyle.Fill;
      heading.Font = new Font("Segoe UI Semibold", 13, FontStyle.Bold);
      heading.TextAlign = ContentAlignment.MiddleLeft;
      heading.ForeColor = palette.Text;
      inputStatus.Dock = DockStyle.Fill;
      inputStatus.TextAlign = ContentAlignment.MiddleRight;
      inputStatus.ForeColor = palette.Muted;
      startButton.Text = "启动 IF";
      StyleButton(startButton);
      header.Controls.Add(heading, 0, 0);
      header.Controls.Add(inputStatus, 1, 0);
      header.Controls.Add(startButton, 2, 0);
      layout.Controls.Add(header, 0, 0);

      var spectrumBlock = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
        Margin = new Padding(0, 3, 0, 10)
      };
      spectrumBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
      spectrumBlock.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      var spectrumToolbar = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false,
        BackColor = palette.Surface, Padding = new Padding(4, 3, 3, 1)
      };
      Label toolbarLabel(string caption) => new()
      {
        Text = caption, AutoSize = true, ForeColor = palette.Muted,
        Margin = new Padding(6, 7, 1, 0)
      };
      spanBox.DropDownStyle = ComboBoxStyle.DropDownList;
      spanBox.Width = 85;
      spanBox.Items.AddRange(new object[] { "12 kHz", "6 kHz", "2 kHz", "1 kHz" });
      spanBox.SelectedIndex = 0;
      averageBox.DropDownStyle = ComboBoxStyle.DropDownList;
      averageBox.Width = 82;
      averageBox.Items.AddRange(new object[] { "OFF", "100 ms", "200 ms", "400 ms" });
      averageBox.SelectedIndex = 2;
      foreach (ComboBox combo in new[] { spanBox, averageBox })
      {
        combo.BackColor = palette.Plot;
        combo.ForeColor = palette.Text;
        combo.FlatStyle = FlatStyle.Flat;
        combo.Margin = new Padding(1, 2, 4, 1);
      }
      waterfallCheck.Text = "WF";
      waterfallCheck.Checked = true;
      peakCheck.Text = "PEAK";
      haloCheck.Text = "HALO";
      bhCheck.Text = "BH4";
      bhCheck.Checked = true;
      foreach (CheckBox option in new[]
        { waterfallCheck, peakCheck, haloCheck, bhCheck })
      {
        option.AutoSize = true;
        option.ForeColor = palette.Text;
        option.Margin = new Padding(6, 6, 1, 0);
      }
      spectrumToolbar.Controls.Add(toolbarLabel("SPAN"));
      spectrumToolbar.Controls.Add(spanBox);
      spectrumToolbar.Controls.Add(toolbarLabel("AVG"));
      spectrumToolbar.Controls.Add(averageBox);
      spectrumToolbar.Controls.Add(peakCheck);
      spectrumToolbar.Controls.Add(waterfallCheck);
      spectrumToolbar.Controls.Add(haloCheck);
      spectrumToolbar.Controls.Add(bhCheck);
      spectrum.Dock = DockStyle.Fill;
      spectrum.Margin = new Padding(0, 2, 0, 0);
      spectrum.ApplyPalette(palette);
      spectrum.SetReferenceLevel(ctx.Settings.Afc.SpectrumRefLevelDb);
      spectrum.TargetSelected += OnSpectrumTargetSelected;
      spectrum.SpanChanged += OnSpanChanged;
      spectrumBlock.Controls.Add(spectrumToolbar, 0, 0);
      spectrumBlock.Controls.Add(spectrum, 0, 1);
      layout.Controls.Add(spectrumBlock, 0, 1);

      var metricRow = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
        Margin = new Padding(0, 1, 0, 9)
      };
      string[] titles = { "多普勒预测", "残余频偏", "AFC 修正" };
      string[] hints = { "卫星下行预测值", "当前测量目标残差", "当前 RX 修正量" };
      for (int i = 0; i < 3; i++)
      {
        metricRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var card = metricCards[i];
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(i == 0 ? 0 : 4, 0,
          i == 2 ? 0 : 4, 0);
        card.Padding = new Padding(12, 6, 8, 5);
        card.BackColor = palette.Surface;
        var tile = new TableLayoutPanel
          { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metricCaptions[i].Text = titles[i];
        metricCaptions[i].Dock = DockStyle.Fill;
        metricCaptions[i].ForeColor = palette.Muted;
        metrics[i].Dock = DockStyle.Fill;
        metrics[i].Font = new Font("Segoe UI Semibold", 14, FontStyle.Bold);
        metrics[i].ForeColor = palette.Accent;
        var help = metricHelp[i];
        help.Text = hints[i];
        help.Dock = DockStyle.Fill;
        help.ForeColor = palette.Muted;
        tile.Controls.Add(metricCaptions[i], 0, 0);
        tile.Controls.Add(metrics[i], 0, 1);
        tile.Controls.Add(help, 0, 2);
        card.Controls.Add(tile);
        metricRow.Controls.Add(card, i, 0);
      }
      layout.Controls.Add(metricRow, 0, 2);

      var controls = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
        Margin = new Padding(0, 0, 0, 10)
      };
      controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
      controls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
      var levels = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
        Padding = new Padding(10, 7, 10, 5),
        Margin = new Padding(0, 0, 5, 0), BackColor = palette.Surface
      };
      levels.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
      levels.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
      levels.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      var levelTitle = levelCaption;
      levelTitle.Text = "AFC 控制等级";
      levelTitle.Dock = DockStyle.Fill;
      levelTitle.ForeColor = palette.Text;
      levelTitle.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
      var segmented = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
      for (int i = 0; i < 3; i++)
        segmented.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
      offButton.Text = "关闭";
      assistButton.Text = "辅助";
      autoButton.Text = "自动";
      foreach (var button in new[] { offButton, assistButton, autoButton })
      {
        StyleButton(button);
        segmented.Controls.Add(button);
      }
      controlHint.Dock = DockStyle.Fill;
      controlHint.ForeColor = palette.Muted;
      levels.Controls.Add(levelTitle, 0, 0);
      levels.Controls.Add(segmented, 0, 1);
      levels.Controls.Add(controlHint, 0, 2);

      var locks = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
        Padding = new Padding(7, 16, 7, 16),
        BackColor = palette.Surface
      };
      locks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      locks.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      lockButton.Text = "锁定当前";
      centerButton.Text = "居中并锁定";
      StyleButton(lockButton);
      StyleButton(centerButton);
      locks.Controls.Add(lockButton, 0, 0);
      locks.Controls.Add(centerButton, 1, 0);
      controls.Controls.Add(levels, 0, 0);
      controls.Controls.Add(locks, 1, 0);
      layout.Controls.Add(controls, 0, 3);

      var diagnostics = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
        Padding = new Padding(10, 7, 10, 5),
        BackColor = palette.Surface
      };
      diagnostics.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
      diagnostics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      stateLabel.Dock = DockStyle.Fill;
      stateLabel.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
      stateDetail.Dock = DockStyle.Fill;
      stateDetail.ForeColor = palette.Muted;
      diagnostics.Controls.Add(stateLabel, 0, 0);
      diagnostics.Controls.Add(stateDetail, 0, 1);
      layout.Controls.Add(diagnostics, 0, 4);
      trackingPage.Controls.Add(layout);

      settingsGrid.Dock = DockStyle.Fill;
      SettingsLanguage.EnsureRegistered();
      settingsGrid.SelectedObject = ctx.Settings.Afc;
      settingsGrid.BackColor = palette.Background;
      settingsGrid.ViewBackColor = palette.Surface;
      settingsGrid.ViewForeColor = palette.Text;
      settingsGrid.HelpBackColor = palette.Surface;
      settingsGrid.HelpForeColor = palette.Text;
      settingsGrid.LineColor = palette.Border;
      settingsGrid.PropertyValueChanged += Settings_PropertyValueChanged;
      advancedPage.Controls.Add(settingsGrid);
      tabs.TabPages.Add(trackingPage);
      tabs.TabPages.Add(advancedPage);
      Controls.Add(tabs);

      startButton.Click += (_, _) => { if (active) Stop(); else Start(); };
      offButton.Click += (_, _) => SwitchControl(AfcControlMode.Off);
      assistButton.Click += (_, _) => SwitchControl(AfcControlMode.Assist);
      autoButton.Click += (_, _) => SwitchControl(AfcControlMode.Auto);
      lockButton.Click += (_, _) => owner.AfcController.LockHere();
      centerButton.Click += (_, _) =>
        owner.AfcController.CenterOn(ctx.Settings.Afc.DesiredOffsetHz);
      owner.AfcTargetInvalidated += OnTargetInvalidated;
      input.ObservationAvailable += OnObservation;
      input.DisplayFrameAvailable += OnDisplayFrame;
      spanBox.SelectedIndexChanged += (_, _) =>
        spectrum.SetSpan(spanBox.SelectedIndex switch
        {
          0 => 12000, 1 => 6000, 2 => 2000, _ => 1000
        });
      averageBox.SelectedIndexChanged += (_, _) => ConfigureDisplay();
      haloCheck.CheckedChanged += (_, _) => ConfigureDisplay();
      bhCheck.CheckedChanged += (_, _) => ConfigureDisplay();
      waterfallCheck.CheckedChanged += (_, _) =>
        spectrum.SetWaterfallVisible(waterfallCheck.Checked);
      peakCheck.CheckedChanged += (_, _) =>
        spectrum.SetPeakHold(peakCheck.Checked);
      VisibleChanged += (_, _) => input.DisplayEnabled = Visible && active;
      ConfigureDisplay();
      timer.Tick += (_, _) => RefreshDisplay();
      timer.Start();
      RefreshDisplay();
    }

    private static string T(string zh, string en) => SettingsLanguage.Text(zh, en);

    private void RefreshLanguage()
    {
      if (renderedLanguage == SettingsLanguage.Current) return;
      renderedLanguage = SettingsLanguage.Current;
      tabs.TabPages[0].Text = T("实时追踪", "Live tracking");
      tabs.TabPages[1].Text = T("高级设置", "Advanced settings");
      levelCaption.Text = T("AFC 控制等级", "AFC control level");
      string[] zhNames = { "多普勒预测", "残余频偏", "AFC 修正" };
      string[] enNames = { "Predicted Doppler", "Residual offset", "AFC correction" };
      string[] zhHelp = { "卫星下行预测值", "当前测量目标残差", "当前 RX 修正量" };
      string[] enHelp = { "Predicted satellite downlink", "Measured target residual", "Current RX correction" };
      for (int i = 0; i < 3; i++)
      {
        metricCaptions[i].Text = T(zhNames[i], enNames[i]);
        metricHelp[i].Text = T(zhHelp[i], enHelp[i]);
      }
      offButton.Text = T("关闭", "OFF");
      assistButton.Text = T("辅助", "ASSIST");
      autoButton.Text = T("自动", "AUTO");
      lockButton.Text = T("锁定当前", "LOCK");
      centerButton.Text = T("居中并锁定", "CENTER & LOCK");
      object? selected = settingsGrid.SelectedObject;
      settingsGrid.SelectedObject = null;
      settingsGrid.SelectedObject = selected;
      tabs.Invalidate();
    }

    private void OnSpanChanged(int hz)
    {
      int index = hz switch { 12000 => 0, 6000 => 1, 2000 => 2, _ => 3 };
      if (spanBox.SelectedIndex != index) spanBox.SelectedIndex = index;
    }

    private void ConfigureDisplay()
    {
      int ms = averageBox.SelectedIndex switch
      {
        0 => 0, 1 => 100, 3 => 400, _ => 200
      };
      input.ConfigureDisplay(bhCheck.Checked, haloCheck.Checked, ms);
    }

    private void OnDisplayFrame(AfcIfDisplayFrame frame)
    {
      // Display samples are independent from the low-rate AFC telemetry.
      // Hidden RX-AFC capture does no UI FFT and requires no repaint.
      if (!active || !Visible || !IsHandleCreated || IsDisposed) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!active || IsDisposed || !Visible ||
              frame.Generation != input.Generation) return;
          displayUtc = frame.EndUtc;
          inputRms = frame.InputRms;
          clipFraction = frame.ClippingFraction;
          noiseFloorDb = frame.NoiseFloorDb;
          var snapshot = owner.AfcController.Snapshot;
          spectrum.SetDisplayFrame(frame,
            snapshot.LastObservationUtc != default &&
              snapshot.Confidence > 0 && snapshot.State == AfcLockState.Locked
              ? snapshot.ObservedIfOffsetHz : null,
            ctx.Settings.Afc.TargetOffsetHz);
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void StyleButton(Button button)
    {
      button.Dock = DockStyle.Fill;
      button.Margin = new Padding(3);
      button.FlatStyle = FlatStyle.Flat;
      button.FlatAppearance.BorderColor = palette.Border;
      button.BackColor = palette.Surface;
      button.ForeColor = palette.Text;
      button.UseVisualStyleBackColor = false;
    }

    private AfcControlMode EffectiveControl() =>
      currentMode == AfcSignalMode.SSB && !ctx.Settings.Afc.SsbPilotEnabled &&
        ctx.Settings.Afc.Control == AfcControlMode.Auto
        ? AfcControlMode.Assist : ctx.Settings.Afc.Control;

    private bool AutoAllowed => owner.RadioLink.HasDownlink &&
      ctx.CatControl.Rx != null &&
      (currentMode != AfcSignalMode.SSB || ctx.Settings.Afc.SsbPilotEnabled);

    internal bool SetAutomaticTracking(bool enabled)
    {
      if (enabled)
      {
        currentMode = owner.CurrentAfcMode;
        if (!AutoAllowed)
        {
          MessageBox.Show(this,
            "RX AFC Auto requires an RX CAT connection, a downlink, and for SSB an explicit stable pilot.",
            "RX AFC", MessageBoxButtons.OK, MessageBoxIcon.Information);
          return false;
        }
        ctx.Settings.Afc.Control = AfcControlMode.Auto;
        if (!active && !Start()) return false;
        owner.AfcController.Configure(currentMode, AfcControlMode.Auto);
        owner.UpdateRxAfcCheckbox(true);
        ctx.Settings.SaveToFile();
        RefreshDisplay();
        return true;
      }

      if (active)
      {
        SwitchControl(AfcControlMode.Assist);
        if (!Visible)
        {
          Stop();
          Close();
        }
      }
      else owner.UpdateRxAfcCheckbox(false);
      return true;
    }

    internal void AbortPolarityCalibration()
    {
      polarityProbe.Reset();
      owner.SetAfcCalibrationOffsetHz(0, false);
    }

    private void OnTargetInvalidated()
    {
      AbortPolarityCalibration();
      if (!active) return;
      input.InvalidateTarget();
      spectrum.ClearHistory();
      currentMode = owner.CurrentAfcMode;
      if (!AutoAllowed && ctx.Settings.Afc.Control == AfcControlMode.Auto)
      {
        ctx.Settings.Afc.Control = AfcControlMode.Assist;
        owner.UpdateRxAfcCheckbox(false);
        ctx.Settings.SaveToFile();
        // No reason to retain an invisible capture when the newly selected
        // satellite has no receiver or lacks a valid SSB reference.
        if (!Visible) { Stop(); Close(); return; }
      }
      owner.AfcController.Configure(currentMode, EffectiveControl());
      if (currentMode == AfcSignalMode.SSB && ctx.Settings.Afc.SsbPilotEnabled)
        owner.AfcController.CenterOn(ctx.Settings.Afc.DesiredOffsetHz);
    }

    private void OnSpectrumTargetSelected(double offset)
    {
      if (!active || currentMode == AfcSignalMode.FM) return;
      ctx.Settings.Afc.TargetOffsetHz = offset;
      input.UpdateTarget(currentMode, ctx.Settings.Afc.SsbPilotEnabled, offset);
      owner.AfcController.Reset("Selected new IF carrier");
      owner.AfcController.Configure(currentMode, EffectiveControl());
      settingsGrid.Refresh();
      ctx.Settings.SaveToFile();
      RefreshDisplay();
    }

    private void Settings_PropertyValueChanged(
      object? sender, PropertyValueChangedEventArgs e)
    {
      string name = e.ChangedItem.PropertyDescriptor?.Name ?? "";
      if (name == nameof(AfcSettings.RfIfSign))
        AbortPolarityCalibration();
      if (active)
      {
        if (name is nameof(AfcSettings.InputDeviceId) or
            nameof(AfcSettings.LoopbackDeviceId) or
            nameof(AfcSettings.InputSource) or
            nameof(AfcSettings.IfReferenceHz))
          Restart();
        else if (name is nameof(AfcSettings.TargetOffsetHz) or
                 nameof(AfcSettings.SsbPilotEnabled))
        {
          input.UpdateTarget(currentMode, ctx.Settings.Afc.SsbPilotEnabled,
            ctx.Settings.Afc.TargetOffsetHz);
          owner.AfcController.Reset("Target parameters changed");
          owner.AfcController.Configure(currentMode, EffectiveControl());
        }
        else if (name == nameof(AfcSettings.Control))
          owner.AfcController.Configure(currentMode, EffectiveControl());
      }
      if (name == nameof(AfcSettings.SpectrumRefLevelDb))
        spectrum.SetReferenceLevel(ctx.Settings.Afc.SpectrumRefLevelDb);
      if (!AutoAllowed && ctx.Settings.Afc.Control == AfcControlMode.Auto)
        ctx.Settings.Afc.Control = AfcControlMode.Assist;
      owner.UpdateRxAfcCheckbox(active && EffectiveControl() == AfcControlMode.Auto);
      ctx.Settings.SaveToFile();
      RefreshDisplay();
    }

    private void SwitchControl(AfcControlMode control)
    {
      if (!active) return;
      if (control == AfcControlMode.Auto && !AutoAllowed) return;
      if (control != AfcControlMode.Auto)
        AbortPolarityCalibration();
      ctx.Settings.Afc.Control = control;
      owner.AfcController.Configure(currentMode, EffectiveControl());
      if (currentMode == AfcSignalMode.SSB && ctx.Settings.Afc.SsbPilotEnabled &&
          control == AfcControlMode.Auto)
        owner.AfcController.CenterOn(ctx.Settings.Afc.DesiredOffsetHz);
      owner.UpdateRxAfcCheckbox(control == AfcControlMode.Auto);
      ctx.Settings.SaveToFile();
      RefreshDisplay();
    }

    private void OnObservation(AfcObservation observation)
    {
      if (!active || !IsHandleCreated || IsDisposed) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!active || IsDisposed || owner.IsDisposed ||
              observation.Mode != currentMode ||
              observation.Generation != input.Generation) return;
          var cfg = ctx.Settings.Afc;
          double readback = owner.GetAfcReadbackAt(observation.SampleUtc);
          // An inferred sign from incidental Doppler motion is not proof.
          // Only a deliberate, restored probe or an explicit operator
          // mapping may open the automatic CAT servo.
          bool blocked = cfg.RfIfSign == 0 &&
            polarityProbe.State != AfcPolarityCalibrator.Phase.Complete;
          owner.AfcController.Observe(observation, readback,
            cfg.RfIfSign, Math.Clamp(cfg.MaxCorrectionHz, 1, 10000),
            Math.Clamp(cfg.FmDeadbandHz, 1, 500),
            Math.Clamp(cfg.ToneDeadbandHz, .5, 100),
            owner.IsAfcTransmitting,
            catReadbackIsTimeAligned: double.IsFinite(readback),
            confirmedOwnAfcReadback:
              owner.IsAfcReadbackConfirmedAt(observation.SampleUtc),
            calibrationInProgress: blocked);
          var snapshot = owner.AfcController.Snapshot;
          bool eligible = cfg.RfIfSign == 0 &&
            EffectiveControl() == AfcControlMode.Auto &&
            !owner.IsAfcTransmitting &&
            snapshot.State == AfcLockState.Locked;
          polarityProbe.Observe(observation, readback,
            owner.IsAfcProbeReadbackConfirmedAt(observation.SampleUtc),
            eligible, ctx.Settings.Cat.TuningStep);
          if (polarityProbe.VerifiedSign != 0)
            owner.AfcController.ConfirmCalibratedSign(
              polarityProbe.VerifiedSign);
          owner.SetAfcCalibrationOffsetHz(polarityProbe.OffsetHz,
            polarityProbe.Active);
          spectrum.SetMeasurement(
            observation.HasAbsoluteReference && observation.Confidence > 0
              ? observation.IfOffsetHz : null,
            cfg.TargetOffsetHz);
          owner.ApplyAfcRxCorrection();
          RefreshDisplay();
        }));
      }
      catch (InvalidOperationException) { }
    }

    private bool Start()
    {
      try
      {
        currentMode = owner.CurrentAfcMode;
        AbortPolarityCalibration();
        var cfg = ctx.Settings.Afc;
        input.Start(cfg.InputDeviceId, cfg.IfReferenceHz, currentMode,
          cfg.SsbPilotEnabled, cfg.TargetOffsetHz,
          cfg.InputSource, cfg.LoopbackDeviceId);
        if (!input.IsRunning)
          throw new InvalidOperationException("12 kHz IF input did not start. Check routing.");
        active = true;
        input.DisplayEnabled = Visible;
        spectrum.ClearHistory();
        displayUtc = default;
        inputRms = double.NaN;
        cfg.Enabled = true;
        if (!AutoAllowed && cfg.Control == AfcControlMode.Auto)
          cfg.Control = AfcControlMode.Assist;
        owner.AfcController.Configure(currentMode, EffectiveControl());
        owner.UpdateRxAfcCheckbox(EffectiveControl() == AfcControlMode.Auto);
        if (currentMode == AfcSignalMode.SSB && cfg.SsbPilotEnabled)
          owner.AfcController.CenterOn(cfg.DesiredOffsetHz);
        ctx.Settings.SaveToFile();
        RefreshDisplay();
        return true;
      }
      catch (Exception ex)
      {
        active = false;
        input.Stop();
        ctx.Settings.Afc.Enabled = false;
        ctx.Settings.Afc.Control = AfcControlMode.Assist;
        owner.AfcController.Reset("IF input failed");
        AbortPolarityCalibration();
        owner.UpdateRxAfcCheckbox(false);
        MessageBox.Show(this, ex.Message, "AFC IF capture",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
        RefreshDisplay();
        return false;
      }
    }

    private void Stop()
    {
      active = false;
      input.DisplayEnabled = false;
      input.Stop();
      spectrum.ClearHistory();
      owner.AfcController.Reset("Stopped by operator");
      AbortPolarityCalibration();
      ctx.Settings.Afc.Enabled = false;
      ctx.Settings.Afc.Control = AfcControlMode.Assist;
      owner.UpdateRxAfcCheckbox(false);
      ctx.Settings.SaveToFile();
      RefreshDisplay();
    }

    private void Restart()
    {
      if (!active) return;
      bool wasAuto = EffectiveControl() == AfcControlMode.Auto;
      Stop();
      if (wasAuto) ctx.Settings.Afc.Control = AfcControlMode.Auto;
      if (Start() && wasAuto) owner.UpdateRxAfcCheckbox(true);
    }

    // Stable controller diagnostic identifiers remain English.
    private static string StateText(AfcLockState state) => state switch
    {
      AfcLockState.Off => T("关闭", "Off"),
      AfcLockState.Acquiring => T("捕获中", "Acquiring"),
      AfcLockState.Locked => T("已锁定", "Locked"),
      AfcLockState.Coasting => T("短时保持", "Coasting"),
      AfcLockState.Hold => T("保持", "Hold"),
      AfcLockState.Lost => T("信号丢失", "Signal lost"),
      AfcLockState.Fault => T("故障", "Fault"),
      _ => state.ToString()
    };

    private static string DetailText(string value) => value switch
    {
      "Control level changed; reacquiring target" => T("控制等级已切换，重新捕获目标", "Control level changed; reacquiring target"),
      "Awaiting fresh IF signal" => T("等待新 IF 信号", "Awaiting fresh IF signal"),
      "Locking currently observed IF frequency" => T("正在锁定当前 IF 频率", "Locking currently observed IF frequency"),
      "Centering selected IF signal" => T("正在将所选 IF 信号移至中心", "Centering selected IF signal"),
      "AFC disabled" => T("AFC 已关闭", "AFC disabled"),
      "SSB relative drift assistance only; no absolute carrier" => T("SSB 仅提供相对漂移辅助，缺少绝对载波参考", "SSB relative drift assistance only; no absolute carrier"),
      "Signal ambiguous, weak, or reference absent; holding" => T("信号不明确、过弱或缺少参考，暂时保持", "Signal ambiguous, weak, or reference absent; holding"),
      "Acquiring selected carrier" => T("正在捕获所选载波", "Acquiring selected carrier"),
      "AFC divergence detected; automatic CAT writes stopped" => T("检测到 AFC 发散，已停止自动 CAT 写频", "AFC divergence detected; automatic CAT writes stopped"),
      "PTT active: AFC frozen" => T("正在发射，AFC 已冻结", "PTT active: AFC frozen"),
      "Learning IF/CAT polarity from observed tuning" => T("正在根据 CAT 调谐学习 IF 频率方向", "Learning IF/CAT polarity from observed tuning"),
      "Tracking reference" => T("正在跟踪参考信号", "Tracking reference"),
      "IF input stale; automatic tuning stopped" => T("IF 数据已过期，自动调谐已停止", "IF input stale; automatic tuning stopped"),
      "No fresh IF measurement; holding correction" => T("暂无新的 IF 测量，保持当前修正", "No fresh IF measurement; holding correction"),
      "Manual tune: AFC target rebased" => T("检测到手动调谐，已重新定位 AFC 目标", "Manual tune: AFC target rebased"),
      _ => value
    };

    private void RefreshDisplay()
    {
      RefreshLanguage();
      owner.AfcController.Tick(DateTime.UtcNow);
      polarityProbe.Watchdog(DateTime.UtcNow,
        active && EffectiveControl() == AfcControlMode.Auto &&
        !owner.IsAfcTransmitting &&
        owner.AfcController.Snapshot.State == AfcLockState.Locked &&
        ctx.CatControl.Rx != null);
      owner.SetAfcCalibrationOffsetHz(polarityProbe.OffsetHz,
        polarityProbe.Active);
      var cfg = ctx.Settings.Afc;
      spectrum.SetReferenceLevel(cfg.SpectrumRefLevelDb);
      if (!ReferenceEquals(settingsGrid.SelectedObject, cfg))
        settingsGrid.SelectedObject = cfg;
      AfcSignalMode mode = owner.CurrentAfcMode;
      if (active && mode != currentMode)
      {
        currentMode = mode;
        input.UpdateTarget(mode, cfg.SsbPilotEnabled, cfg.TargetOffsetHz);
        spectrum.ClearHistory();
        owner.AfcController.Reset("Radio mode changed");
        if (!AutoAllowed && cfg.Control == AfcControlMode.Auto)
        {
          cfg.Control = AfcControlMode.Assist;
          owner.UpdateRxAfcCheckbox(false);
          ctx.Settings.SaveToFile();
        }
        owner.AfcController.Configure(mode, EffectiveControl());
      }
      // CAT/disconnected receiver can change without changing the signal
      // mode. Do not leave the strip checked when Auto can no longer run.
      if (active && cfg.Control == AfcControlMode.Auto && !AutoAllowed)
      {
        cfg.Control = AfcControlMode.Assist;
        owner.AfcController.Configure(currentMode, AfcControlMode.Assist);
        owner.UpdateRxAfcCheckbox(false);
        ctx.Settings.SaveToFile();
        if (!Visible) { Stop(); Close(); return; }
      }
      var s = owner.AfcController.Snapshot;
      bool freshCat = double.IsFinite(owner.GetAfcReadbackAt(DateTime.UtcNow));
      bool hasCat = owner.TryGetAfcLatestReadbackAge(out double catAge);
      string catZh = freshCat ? "读回有效" :
        hasCat ? $"读回过期 {catAge:F1}s" : "等待读回";
      string catEn = freshCat ? "readback OK" :
        hasCat ? $"stale readback {catAge:F1}s" : "awaiting readback";
      heading.Text = $"{currentMode}  ·  IC-9700 / 12 kHz IF";
      inputStatus.Text = input.IsRunning
        ? double.IsFinite(inputRms) && inputRms > 0
          ? $"● IF RMS {20 * Math.Log10(inputRms):F0} dBFS"
          : T("● IF 采集已启动", "● IF capture running")
        : T("○ IF 未连接", "○ IF disconnected");
      inputStatus.ForeColor = input.IsRunning ? palette.Good : palette.Muted;
      metrics[0].Text = $"{owner.PredictedDownlinkDopplerHz:+0;-0;0} Hz";
      metrics[1].Text = $"{s.ErrorHz:+0.0;-0.0;0} Hz";
      metrics[2].Text = $"{s.CorrectionHz:+0.0;-0.0;0} Hz";
      metrics[1].ForeColor = s.State == AfcLockState.Locked
        ? palette.Good : palette.Warning;
      stateLabel.Text = active
        ? T(
            $"状态: {StateText(s.State)}   ·   置信度 {s.Confidence:P0}   ·   CAT {catZh}",
            $"State: {StateText(s.State)}  ·  Confidence {s.Confidence:P0}  ·  CAT {catEn}")
        : T("状态: 关闭  ·  IF 输入尚未启动", "State: Off  ·  IF input not started");
      stateLabel.ForeColor = active &&
        s.State == AfcLockState.Locked ? palette.Good : palette.Warning;
      string calibrationZh = polarityProbe.State switch
      {
        AfcPolarityCalibrator.Phase.Probing => "方向校准: 测试调谐",
        AfcPolarityCalibrator.Phase.Restoring => "方向校准: 恢复并验证",
        AfcPolarityCalibrator.Phase.Complete => "方向校准: 已验证",
        AfcPolarityCalibrator.Phase.Failed => "方向校准: 失败，已禁止自动修正",
        _ => "方向校准: 等待可靠 CAT/IF 数据"
      };
      string calibrationEn = polarityProbe.Detail;
      stateDetail.Text = active
        ? T(
            $"{DetailText(s.Detail)}  ·  搜索 {cfg.TargetOffsetHz:+0;-0;0} Hz  ·  居中 {cfg.DesiredOffsetHz:+0;-0;0} Hz  ·  RF/IF 方向 {(s.MappingSign == 0 ? "待确认" : s.MappingSign.ToString("+0;-0"))}" +
              $"  ·  {calibrationZh}  ·  底噪 {noiseFloorDb:F0} dBFS  ·  削波 {clipFraction:P1}",
            $"{DetailText(s.Detail)}  ·  Search {cfg.TargetOffsetHz:+0;-0;0} Hz  ·  Center {cfg.DesiredOffsetHz:+0;-0;0} Hz  ·  RF/IF sign {(s.MappingSign == 0 ? "unknown" : s.MappingSign.ToString("+0;-0"))}" +
              $"  ·  {calibrationEn}  ·  Noise {noiseFloorDb:F0} dBFS  ·  Clipping {clipFraction:P1}")
        : T("请在「高级设置」配置真正的 12 kHz IF 输入，再启动 IF 采集。",
            "Select a real 12 kHz IF input under Advanced settings, then start capture.");
      startButton.Text = active ? T("停止 IF", "STOP IF") : T("启动 IF", "START IF");
      bool canLock = active && currentMode != AfcSignalMode.FM;
      lockButton.Enabled = centerButton.Enabled = canLock;
      spectrum.SetTargetInteraction(canLock);
      offButton.Enabled = assistButton.Enabled = active;
      autoButton.Enabled = active && AutoAllowed;
      AfcControlMode level = active ? EffectiveControl() : AfcControlMode.Off;
      foreach (Button b in new[] { offButton, assistButton, autoButton })
      {
        bool selected = b == offButton && level == AfcControlMode.Off ||
          b == assistButton && level == AfcControlMode.Assist ||
          b == autoButton && level == AfcControlMode.Auto;
        b.BackColor = selected ? palette.Accent : palette.Surface;
        b.ForeColor = selected ? palette.SelectedText : palette.Text;
      }
      controlHint.Text = level switch
      {
        AfcControlMode.Auto => T("自动修正仅作用于 RX；PTT 时冻结", "Automatic RX correction, frozen during PTT"),
        AfcControlMode.Assist => T("仅监测 / 不进行 AFC 自动写频", "Monitor only; no automatic CAT tuning"),
        _ => T("已关闭 AFC 自动控制", "Automatic AFC control disabled")
      };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      if (e.CloseReason == CloseReason.UserClosing && owner.RxAfcChecked && active)
      {
        // The strip's RX AFC checkbox owns the automatic capture lifetime;
        // closing a *visible* console must not silently disable that switch.
        e.Cancel = true;
        Hide();
        return;
      }
      timer.Stop();
      if (active) Stop();
      owner.AfcTargetInvalidated -= OnTargetInvalidated;
      spectrum.TargetSelected -= OnSpectrumTargetSelected;
      input.ObservationAvailable -= OnObservation;
      input.DisplayFrameAvailable -= OnDisplayFrame;
      spectrum.SpanChanged -= OnSpanChanged;
      input.Dispose();
      timer.Dispose();
      base.OnFormClosing(e);
    }
  }
}
