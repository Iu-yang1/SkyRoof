using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkyRoof.AFC
{
  /// <summary>Explicitly opened AFC console; closing releases the IF input.</summary>
  public sealed class AfcControlForm : Form
  {
    private readonly Context ctx;
    private readonly FrequencyWidget owner;
    private readonly AfcIfInput input = new();
    private readonly PropertyGrid settingsGrid = new();
    private readonly AfcSpectrumView spectrum = new();
    private readonly TabControl tabs = new();
    private readonly Label[] metricValues = { new(), new(), new() };
    private readonly Button offButton = new();
    private readonly Button centerButton = new();
    private readonly Label stateLabel = new();
    private readonly Label measureLabel = new();
    private readonly Button startButton = new();
    private readonly Button assistButton = new();
    private readonly Button autoButton = new();
    private readonly Button lockButton = new();
    private readonly Button centerButton = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private AfcSignalMode currentMode;
    private bool active;

    public AfcControlForm(Context ctx, FrequencyWidget owner)
    {
      this.ctx = ctx;
      this.owner = owner;
      currentMode = owner.CurrentAfcMode;
      Text = "SkyRoof - FM / CW / SSB AFC";
      MinimumSize = new Size(470, 470);
      Size = new Size(555, 600);
      StartPosition = FormStartPosition.CenterParent;
      BackColor = Color.FromArgb(13, 17, 23);
      ForeColor = Color.FromArgb(230, 237, 243);
      Font = new Font("Segoe UI", 9f);
      AutoScaleMode = AutoScaleMode.Dpi;
      MinimumSize = new Size(685, 525);
      Size = new Size(815, 650);
      tabs.Dock = DockStyle.Fill;
      var trackingPage = new TabPage("实时追踪") { BackColor = BackColor };
      var advancedPage = new TabPage("高级设置") { BackColor = BackColor };
      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
        Padding = new Padding(12, 10, 12, 12), BackColor = BackColor
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));

      stateLabel.Dock = DockStyle.Fill;
      stateLabel.ForeColor = ForeColor;
      stateLabel.Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold);
      stateLabel.TextAlign = ContentAlignment.MiddleLeft;
      layout.Controls.Add(stateLabel, 0, 0);

      spectrum.Dock = DockStyle.Fill;
      spectrum.Margin = new Padding(0, 2, 0, 8);
      layout.Controls.Add(spectrum, 0, 1);

      var metricRow = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
        BackColor = Color.FromArgb(22, 27, 34)
      };
      string[] captions = { "实测 IF 频偏", "目标误差", "RX AFC 修正量" };
      for (int n = 0; n < 3; n++)
      {
        metricRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        var tile = new TableLayoutPanel
        {
          Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
          Padding = new Padding(8, 5, 4, 5)
        };
        tile.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        tile.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var caption = new Label { Text = captions[n], Dock = DockStyle.Fill,
          ForeColor = Color.FromArgb(139, 148, 158) };
        metricValues[n].Dock = DockStyle.Fill;
        metricValues[n].ForeColor = ForeColor;
        metricValues[n].Font = new Font("Segoe UI Semibold", 13, FontStyle.Bold);
        tile.Controls.Add(caption, 0, 0);
        tile.Controls.Add(metricValues[n], 0, 1);
        metricRow.Controls.Add(tile, n, 0);
      }
      layout.Controls.Add(metricRow, 0, 2);

      var actions = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1
      };
      for (int n = 0; n < 6; n++)
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
      startButton.Text = "启动 IF";
      offButton.Text = "关闭";
      assistButton.Text = "辅助";
      autoButton.Text = "自动";
      lockButton.Text = "锁定当前";
      centerButton.Text = "居中并锁定";
      Control[] buttons = {
        startButton, offButton, assistButton, autoButton,
        lockButton, centerButton
      };
      for (int n = 0; n < buttons.Length; n++)
      {
        var b = (Button)buttons[n];
        b.Dock = DockStyle.Fill;
        b.Margin = new Padding(2, 4, 2, 4);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(65, 73, 83);
        b.UseVisualStyleBackColor = false;
        b.BackColor = Color.FromArgb(33, 38, 45);
        b.ForeColor = ForeColor;
        actions.Controls.Add(b, n, 0);
      }
      layout.Controls.Add(actions, 0, 3);

      var message = new TableLayoutPanel
      {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
        Padding = new Padding(9, 5, 9, 5),
        BackColor = Color.FromArgb(22, 27, 34)
      };
      message.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
      message.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      status.Dock = DockStyle.Fill;
      status.ForeColor = Color.FromArgb(210, 153, 34);
      status.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
      measurements.Dock = DockStyle.Fill;
      measurements.ForeColor = Color.FromArgb(139, 148, 158);
      message.Controls.Add(status, 0, 0);
      message.Controls.Add(measurements, 0, 1);
      layout.Controls.Add(message, 0, 4);
      trackingPage.Controls.Add(layout);

      settingsGrid.Dock = DockStyle.Fill;
      settingsGrid.SelectedObject = ctx.Settings.Afc;
      settingsGrid.PropertyValueChanged += Settings_PropertyValueChanged;
      advancedPage.Controls.Add(settingsGrid);
      tabs.TabPages.Add(trackingPage);
      tabs.TabPages.Add(advancedPage);
      Controls.Add(tabs);
      startButton.Click += (_, _) => { if (active) Stop(); else Start(); };
      assistButton.Click += (_, _) => SwitchControl(AfcControlMode.Assist);
      autoButton.Click += (_, _) => SwitchControl(AfcControlMode.Auto);
      lockButton.Click += (_, _) => owner.AfcController.LockHere();
      centerButton.Click += (_, _) =>
        owner.AfcController.CenterOn(ctx.Settings.Afc.DesiredOffsetHz);
      offButton.Click += (_, _) => SwitchControl(AfcControlMode.Off);
      spectrum.TargetSelected += OnSpectrumTargetSelected;
      owner.AfcTargetInvalidated += OnTargetInvalidated;
      centerButton.Click += (_, _) => owner.AfcController.CenterOn(
        ctx.Settings.Afc.TargetOffsetHz);
      input.ObservationAvailable += OnObservation;
      timer.Tick += (_, _) => RefreshDisplay();
      timer.Start();
      RefreshDisplay();
    }

    private void SwitchControl(AfcControlMode control)
    {
      if (!active) return;
      if (control == AfcControlMode.Auto &&
          currentMode == AfcSignalMode.SSB && !ctx.Settings.Afc.SsbPilotEnabled)
        return;
      ctx.Settings.Afc.Control = control;
      ctx.Settings.SaveToFile();
      owner.AfcController.Configure(currentMode, control);
      if (currentMode == AfcSignalMode.SSB && ctx.Settings.Afc.SsbPilotEnabled)
        owner.AfcController.CenterOn(ctx.Settings.Afc.TargetOffsetHz);
      RefreshDisplay();
    }

    private void OnObservation(AfcObservation obs)
    {
      if (!active || !IsHandleCreated || IsDisposed) return;
      try
      {
        BeginInvoke((Action)(() =>
        {
          if (!active || IsDisposed || owner.IsDisposed || obs.Mode != currentMode)
            return;
          var cfg = ctx.Settings.Afc;
          owner.AfcController.Observe(obs, owner.GetAfcRxReadbackRf(),
            cfg.RfIfSign, Math.Clamp(cfg.MaxCorrectionHz, 1, 10000),
            Math.Clamp(cfg.FmDeadbandHz, 1, 500),
            Math.Clamp(cfg.ToneDeadbandHz, 0.5, 100),
            owner.IsAfcTransmitting);
          owner.ApplyAfcRxCorrection();
        }));
      }
      catch (InvalidOperationException) { }
    }

    private void Start()
    {
      try
      {
        currentMode = owner.CurrentAfcMode;
        var cfg = ctx.Settings.Afc;
        if (currentMode == AfcSignalMode.SSB &&
            cfg.Control == AfcControlMode.Auto && !cfg.SsbPilotEnabled)
          cfg.Control = AfcControlMode.Assist;
        input.Start(cfg.InputDeviceId, cfg.IfReferenceHz, currentMode,
          cfg.SsbPilotEnabled, cfg.TargetOffsetHz,
          cfg.InputSource, cfg.LoopbackDeviceId);
        active = true;
        cfg.Enabled = true;
        owner.AfcController.Configure(currentMode, cfg.Control);
        if (currentMode == AfcSignalMode.SSB && cfg.SsbPilotEnabled)
          owner.AfcController.CenterOn(cfg.TargetOffsetHz);
        ctx.Settings.SaveToFile();
        RefreshDisplay();
      }
      catch (Exception ex)
      {
        active = false;
        owner.AfcController.Reset("IF capture failed");
        MessageBox.Show(this, ex.Message, "AFC IF input",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private void Restart() { Stop(); Start(); }

    private void Stop()
    {
      active = false;
      input.Stop();
      owner.AfcController.Reset("Stopped by operator");
      ctx.Settings.Afc.Enabled = false;
      ctx.Settings.SaveToFile();
      RefreshDisplay();
    }

    private void RefreshDisplay()
    {
      owner.AfcController.Tick(DateTime.UtcNow);
      AfcSignalMode mode = owner.CurrentAfcMode;
      if (active && mode != currentMode)
      {
        currentMode = mode;
        input.UpdateTarget(mode, ctx.Settings.Afc.SsbPilotEnabled,
          ctx.Settings.Afc.TargetOffsetHz);
        owner.AfcController.Configure(mode, ctx.Settings.Afc.Control);
      }
      var s = owner.AfcController.Snapshot;
      stateLabel.Text = "Mode: " + currentMode + "    " +
        (active ? s.State.ToString().ToUpperInvariant() : "OFF") +
        "    IF: " + (input.IsRunning ? "running" : "stopped");
      measureLabel.Text = "Observed IF offset: " +
        s.ObservedIfOffsetHz.ToString("+0.0;-0.0;0") +
        " Hz     Error: " + s.ErrorHz.ToString("+0.0;-0.0;0") +
        " Hz\nRX AFC: " + s.CorrectionHz.ToString("+0.0;-0.0;0") +
        " Hz     Confidence: " + s.Confidence.ToString("P0") +
        "\n" + s.Detail;
      startButton.Text = active ? "Stop IF" : "Start IF";
      assistButton.Enabled = active;
      autoButton.Enabled = active &&
        (currentMode != AfcSignalMode.SSB || ctx.Settings.Afc.SsbPilotEnabled);
      lockButton.Enabled = active;
      centerButton.Enabled = active;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      timer.Stop();
      active = false;
      owner.AfcController.Reset("AFC console closed");
      ctx.Settings.Afc.Enabled = false;
      ctx.Settings.SaveToFile();
      input.ObservationAvailable -= OnObservation;
      input.Dispose();
      timer.Dispose();
      base.OnFormClosing(e);
    }
  }
}
