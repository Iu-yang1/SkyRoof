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
      var layout = new TableLayoutPanel { Dock = DockStyle.Fill,
        ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
      layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

      var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill,
        WrapContents = false };
      startButton.Text = "Start IF"; startButton.Width = 85;
      assistButton.Text = "Assist"; assistButton.Width = 78;
      autoButton.Text = "Auto"; autoButton.Width = 75;
      lockButton.Text = "Lock Here"; lockButton.Width = 88;
      centerButton.Text = "Center & Lock"; centerButton.Width = 108;
      buttons.Controls.AddRange(new Control[]
        { startButton, assistButton, autoButton, lockButton, centerButton });
      layout.Controls.Add(buttons, 0, 0);

      stateLabel.Dock = DockStyle.Fill;
      stateLabel.TextAlign = ContentAlignment.MiddleLeft;
      stateLabel.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
      layout.Controls.Add(stateLabel, 0, 1);

      measureLabel.Dock = DockStyle.Fill;
      measureLabel.TextAlign = ContentAlignment.MiddleLeft;
      layout.Controls.Add(measureLabel, 0, 2);

      settingsGrid.Dock = DockStyle.Fill;
      settingsGrid.SelectedObject = ctx.Settings.Afc;
      settingsGrid.PropertyValueChanged += (_, _) =>
      {
        ctx.Settings.SaveToFile();
        if (active) Restart();
      };
      layout.Controls.Add(settingsGrid, 0, 3);
      Controls.Add(layout);

      startButton.Click += (_, _) => { if (active) Stop(); else Start(); };
      assistButton.Click += (_, _) => SwitchControl(AfcControlMode.Assist);
      autoButton.Click += (_, _) => SwitchControl(AfcControlMode.Auto);
      lockButton.Click += (_, _) => owner.AfcController.LockHere();
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
