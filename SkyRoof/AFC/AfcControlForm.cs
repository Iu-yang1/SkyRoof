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
    private readonly AfcThemePalette palette = AfcThemePalette.Current;
    private readonly PropertyGrid settingsGrid = new();
    private readonly AfcSpectrumView spectrum = new();
    private readonly TabControl tabs = new();
    private readonly Label heading = new();
    private readonly Label inputStatus = new();
    private readonly Label stateLabel = new();
    private readonly Label stateDetail = new();
    private readonly Label[] metrics = { new(), new(), new() };
    private readonly Label[] metricCaptions = { new(), new(), new() };
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

      spectrum.Dock = DockStyle.Fill;
      spectrum.Margin = new Padding(0, 3, 0, 10);
      spectrum.ApplyPalette(palette);
      spectrum.TargetSelected += OnSpectrumTargetSelected;
      layout.Controls.Add(spectrum, 0, 1);

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
        var help = new Label
          { Text = hints[i], Dock = DockStyle.Fill, ForeColor = palette.Muted };
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
      var levelTitle = new Label
      {
        Text = "AFC 控制等级", Dock = DockStyle.Fill,
        ForeColor = palette.Text, Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold)
      };
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
      timer.Tick += (_, _) => RefreshDisplay();
      timer.Start();
      RefreshDisplay();
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

    private void OnTargetInvalidated()
    {
      if (!active) return;
      input.InvalidateTarget();
      currentMode = owner.CurrentAfcMode;
      if (!AutoAllowed && ctx.Settings.Afc.Control == AfcControlMode.Auto)
      {
        ctx.Settings.Afc.Control = AfcControlMode.Assist;
        owner.UpdateRxAfcCheckbox(false);
        ctx.Settings.SaveToFile();
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
          owner.AfcController.Observe(observation, readback,
            cfg.RfIfSign, Math.Clamp(cfg.MaxCorrectionHz, 1, 10000),
            Math.Clamp(cfg.FmDeadbandHz, 1, 500),
            Math.Clamp(cfg.ToneDeadbandHz, .5, 100),
            owner.IsAfcTransmitting,
            catReadbackIsTimeAligned: double.IsFinite(readback),
            confirmedOwnAfcReadback:
              owner.IsAfcReadbackConfirmedAt(observation.SampleUtc));
          spectrum.SetSpectrum(observation.SpectrumDb,
            observation.SpectrumMinHz, observation.SpectrumStepHz,
            observation.HasAbsoluteReference ? observation.IfOffsetHz : null,
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
        var cfg = ctx.Settings.Afc;
        input.Start(cfg.InputDeviceId, cfg.IfReferenceHz, currentMode,
          cfg.SsbPilotEnabled, cfg.TargetOffsetHz,
          cfg.InputSource, cfg.LoopbackDeviceId);
        if (!input.IsRunning)
          throw new InvalidOperationException("12 kHz IF input did not start. Check routing.");
        active = true;
        cfg.Enabled = true;
        owner.AfcController.Configure(currentMode, EffectiveControl());
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
      input.Stop();
      owner.AfcController.Reset("Stopped by operator");
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

    private void RefreshDisplay()
    {
      owner.AfcController.Tick(DateTime.UtcNow);
      var cfg = ctx.Settings.Afc;
      if (!ReferenceEquals(settingsGrid.SelectedObject, cfg))
        settingsGrid.SelectedObject = cfg;
      AfcSignalMode mode = owner.CurrentAfcMode;
      if (active && mode != currentMode)
      {
        currentMode = mode;
        input.UpdateTarget(mode, cfg.SsbPilotEnabled, cfg.TargetOffsetHz);
        owner.AfcController.Reset("Radio mode changed");
        if (!AutoAllowed && cfg.Control == AfcControlMode.Auto)
        {
          cfg.Control = AfcControlMode.Assist;
          owner.UpdateRxAfcCheckbox(false);
          ctx.Settings.SaveToFile();
        }
        owner.AfcController.Configure(mode, EffectiveControl());
      }
      var s = owner.AfcController.Snapshot;
      bool freshCat = double.IsFinite(owner.GetAfcReadbackAt(DateTime.UtcNow));
      heading.Text = $"{currentMode}  ·  IC-9700 / 12 kHz IF";
      inputStatus.Text = input.IsRunning ? "● IF 连接正常" : "○ IF 未连接";
      inputStatus.ForeColor = input.IsRunning ? palette.Good : palette.Muted;
      metrics[0].Text = $"{owner.PredictedDownlinkDopplerHz:+0;-0;0} Hz";
      metrics[1].Text = $"{s.ErrorHz:+0.0;-0.0;0} Hz";
      metrics[2].Text = $"{s.CorrectionHz:+0.0;-0.0;0} Hz";
      metrics[1].ForeColor = s.State == AfcLockState.Locked
        ? palette.Good : palette.Warning;
      stateLabel.Text = active
        ? $"状态: {s.State}   ·   置信度 {s.Confidence:P0}   ·   CAT {(freshCat ? "读回有效" : "等待读回")}"
        : "状态: OFF  ·  IF 输入尚未启动";
      stateLabel.ForeColor = active &&
        s.State == AfcLockState.Locked ? palette.Good : palette.Warning;
      stateDetail.Text = active
        ? $"{s.Detail}  ·  搜索 {cfg.TargetOffsetHz:+0;-0;0} Hz  ·  居中 {cfg.DesiredOffsetHz:+0;-0;0} Hz  ·  RF/IF 方向 {(s.MappingSign == 0 ? "待确认" : s.MappingSign.ToString("+0;-0"))}"
        : "请在「高级设置」配置真正的 12 kHz IF 输入，再启动 IF 采集。";
      startButton.Text = active ? "停止 IF" : "启动 IF";
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
        AfcControlMode.Auto => "自动修正仅作用于 RX；PTT 时冻结",
        AfcControlMode.Assist => "仅监测 / 不进行 AFC 自动写频",
        _ => "已关闭 AFC 自动控制"
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
      input.Dispose();
      timer.Dispose();
      base.OnFormClosing(e);
    }
  }
}
