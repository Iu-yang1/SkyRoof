using System.Drawing;
using System.Windows.Forms;
using Serilog;
using WeifenLuo.WinFormsUI.Docking;

namespace SkyRoof
{
  public class FrequencyControlPanel : DockContent
  {
    private Context? ctx;
    private readonly FrequencyEntryForm FrequencyDialog = new();

    private readonly Label SelectionLabel = new();
    private readonly GroupBox DownlinkGroup = new();
    private readonly GroupBox UplinkGroup = new();

    private readonly Label DownlinkBaseValue = new();
    private readonly Label UplinkBaseValue = new();
    private readonly Label DownlinkDatabaseValue = new();
    private readonly Label UplinkDatabaseValue = new();
    private readonly Label DownlinkCorrectionValue = new();
    private readonly Label UplinkCorrectionValue = new();
    private readonly Button DownlinkEditBtn = new();
    private readonly Button UplinkEditBtn = new();
    private readonly Button DownlinkResetBtn = new();
    private readonly Button UplinkResetBtn = new();

    private readonly GroupBox TuningGroup = new();
    private readonly Label TuningFrequencyLabel = new();
    private readonly Label TuningModeLabel = new();
    private readonly FrequencyTuningBar TuningBar = new();
    private readonly Label TuningHelpLabel = new();

    public FrequencyControlPanel()
    {
      InitializeUi();
    }

    public FrequencyControlPanel(Context ctx) : this()
    {
      this.ctx = ctx;
      Log.Information("Creating FrequencyControlPanel");

      ctx.FrequencyControlPanel = this;
      ctx.MainForm.FrequencyControlMNU.Checked = true;
      RefreshFromRadioLink();
    }

    private void InitializeUi()
    {
      Text = "Frequency Control";
      Name = "FrequencyControlPanel";
      ClientSize = new Size(760, 390);
      MinimumSize = new Size(560, 330);
      FormClosing += FrequencyControlPanel_FormClosing;

      var root = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        Padding = new Padding(10),
        ColumnCount = 1,
        RowCount = 3
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
      root.RowStyles.Add(new RowStyle(SizeType.Percent, 44));

      SelectionLabel.Dock = DockStyle.Fill;
      SelectionLabel.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
      SelectionLabel.TextAlign = ContentAlignment.MiddleLeft;
      SelectionLabel.AutoEllipsis = true;
      SelectionLabel.Text = "Frequency Control";
      root.Controls.Add(SelectionLabel, 0, 0);

      var baseLayout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        RowCount = 1,
        Padding = new Padding(0, 0, 0, 6)
      };
      baseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      baseLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      baseLayout.Controls.Add(DownlinkGroup, 0, 0);
      baseLayout.Controls.Add(UplinkGroup, 1, 0);
      root.Controls.Add(baseLayout, 0, 1);

      ConfigureBaseGroup(
        DownlinkGroup, "Downlink Base",
        DownlinkBaseValue, DownlinkDatabaseValue, DownlinkCorrectionValue,
        DownlinkEditBtn, DownlinkResetBtn,
        (_, _) => EditBaseFrequency(false),
        (_, _) => ResetBaseFrequency(false));

      ConfigureBaseGroup(
        UplinkGroup, "Uplink Base",
        UplinkBaseValue, UplinkDatabaseValue, UplinkCorrectionValue,
        UplinkEditBtn, UplinkResetBtn,
        (_, _) => EditBaseFrequency(true),
        (_, _) => ResetBaseFrequency(true));

      DownlinkBaseValue.Click += (_, _) => EditBaseFrequency(false);
      UplinkBaseValue.Click += (_, _) => EditBaseFrequency(true);

      ConfigureTuningGroup();
      root.Controls.Add(TuningGroup, 0, 2);

      Controls.Add(root);
    }

    private static void ConfigureBaseGroup(
      GroupBox group,
      string title,
      Label baseValue,
      Label databaseValue,
      Label correctionValue,
      Button editBtn,
      Button resetBtn,
      EventHandler editHandler,
      EventHandler resetHandler)
    {
      group.Text = title;
      group.Dock = DockStyle.Fill;
      group.Margin = new Padding(4);

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        Padding = new Padding(8, 5, 8, 7),
        ColumnCount = 2,
        RowCount = 4
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

      baseValue.BackColor = Color.Black;
      baseValue.ForeColor = Color.Aqua;
      baseValue.Cursor = Cursors.Hand;
      baseValue.Font = new Font("Microsoft Sans Serif", 16F);
      baseValue.Text = "000,000,000";
      baseValue.TextAlign = ContentAlignment.MiddleCenter;
      baseValue.Dock = DockStyle.Fill;
      baseValue.Margin = new Padding(0, 0, 0, 7);
      layout.SetColumnSpan(baseValue, 2);
      layout.Controls.Add(baseValue, 0, 0);

      var dbCaption = MakeCaptionLabel("Database reference");
      layout.Controls.Add(dbCaption, 0, 1);
      ConfigureValueLabel(databaseValue);
      layout.Controls.Add(databaseValue, 1, 1);

      var correctionCaption = MakeCaptionLabel("Saved correction");
      layout.Controls.Add(correctionCaption, 0, 2);
      ConfigureValueLabel(correctionValue);
      layout.Controls.Add(correctionValue, 1, 2);

      var buttons = new FlowLayoutPanel
      {
        Dock = DockStyle.Fill,
        AutoSize = true,
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        Margin = new Padding(0, 7, 0, 0)
      };
      editBtn.Text = "Edit Base...";
      editBtn.AutoSize = true;
      editBtn.Click += editHandler;
      resetBtn.Text = "Reset to Database";
      resetBtn.AutoSize = true;
      resetBtn.Click += resetHandler;
      buttons.Controls.Add(editBtn);
      buttons.Controls.Add(resetBtn);
      layout.SetColumnSpan(buttons, 2);
      layout.Controls.Add(buttons, 0, 3);

      group.Controls.Add(layout);
    }

    private static Label MakeCaptionLabel(string text)
    {
      return new Label
      {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 2, 6, 2)
      };
    }

    private static void ConfigureValueLabel(Label label)
    {
      label.AutoSize = true;
      label.Anchor = AnchorStyles.Right;
      label.TextAlign = ContentAlignment.MiddleRight;
      label.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
      label.Margin = new Padding(6, 2, 0, 2);
    }

    private void ConfigureTuningGroup()
    {
      TuningGroup.Text = "HRD-style Tuning Ruler";
      TuningGroup.Dock = DockStyle.Fill;
      TuningGroup.Margin = new Padding(4);

      var layout = new TableLayoutPanel
      {
        Dock = DockStyle.Fill,
        Padding = new Padding(8, 5, 8, 6),
        ColumnCount = 2,
        RowCount = 3
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

      TuningFrequencyLabel.Dock = DockStyle.Fill;
      TuningFrequencyLabel.Font = new Font("Microsoft Sans Serif", 14F);
      TuningFrequencyLabel.Text = "000,000,000 Hz";
      TuningFrequencyLabel.TextAlign = ContentAlignment.MiddleLeft;
      TuningFrequencyLabel.Margin = new Padding(0, 0, 6, 5);
      layout.Controls.Add(TuningFrequencyLabel, 0, 0);

      TuningModeLabel.Dock = DockStyle.Fill;
      TuningModeLabel.TextAlign = ContentAlignment.MiddleRight;
      TuningModeLabel.Margin = new Padding(6, 0, 0, 5);
      layout.Controls.Add(TuningModeLabel, 1, 0);

      TuningBar.Dock = DockStyle.Fill;
      TuningBar.MinimumSize = new Size(100, 54);
      TuningBar.TuneDeltaRequested += TuningBar_TuneDeltaRequested;
      layout.SetColumnSpan(TuningBar, 2);
      layout.Controls.Add(TuningBar, 0, 1);

      TuningHelpLabel.AutoSize = true;
      TuningHelpLabel.Text = "Drag horizontally or use the mouse wheel. Ctrl = RIT; Alt + wheel = 500 Hz step.";
      TuningHelpLabel.ForeColor = SystemColors.GrayText;
      TuningHelpLabel.Margin = new Padding(0, 5, 0, 0);
      layout.SetColumnSpan(TuningHelpLabel, 2);
      layout.Controls.Add(TuningHelpLabel, 0, 2);

      TuningGroup.Controls.Add(layout);
    }

    internal void RefreshFromRadioLink()
    {
      if (ctx == null || IsDisposed) return;

      var link = ctx.FrequencyControl.RadioLink;
      bool satellite = !link.IsTerrestrial && link.Tx != null && link.TxCust != null;

      if (satellite)
      {
        string satelliteName = link.Sat?.name ?? "Satellite";
        string transmitterName = string.IsNullOrWhiteSpace(link.Tx?.description)
          ? "Selected transmitter"
          : link.Tx.description;
        SelectionLabel.Text = $"{satelliteName} — {transmitterName}";
      }
      else
        SelectionLabel.Text = "Terrestrial tuning — select a satellite transmitter to edit saved Base frequencies";

      DownlinkGroup.Enabled = satellite;
      UplinkGroup.Enabled = satellite && link.HasUplink;

      if (satellite)
      {
        DownlinkBaseValue.Text = $"{link.BaseDownlinkFrequency:n0}";
        DownlinkDatabaseValue.Text = $"{link.DatabaseDownlinkBaseFrequency:n0} Hz";
        DownlinkCorrectionValue.Text = $"{link.DownlinkBaseOffset:+0;-0;0} Hz";

        if (link.HasUplink)
        {
          UplinkBaseValue.Text = $"{link.BaseUplinkFrequency:n0}";
          UplinkDatabaseValue.Text = $"{link.DatabaseUplinkBaseFrequency:n0} Hz";
          UplinkCorrectionValue.Text = $"{link.UplinkBaseOffset:+0;-0;0} Hz";
        }
        else
        {
          UplinkBaseValue.Text = "No Uplink";
          UplinkDatabaseValue.Text = "—";
          UplinkCorrectionValue.Text = "—";
        }
      }
      else
      {
        DownlinkBaseValue.Text = "—";
        DownlinkDatabaseValue.Text = "—";
        DownlinkCorrectionValue.Text = "—";
        UplinkBaseValue.Text = "—";
        UplinkDatabaseValue.Text = "—";
        UplinkCorrectionValue.Text = "—";
      }

      Color rxColor = GetFrequencyColor(link.CorrectedDownlinkFrequency, ctx.CatControl.Rx?.IsRunning == true);
      DownlinkBaseValue.ForeColor = satellite ? rxColor : Color.Gray;

      Color txColor = GetFrequencyColor(link.CorrectedUplinkFrequency, ctx.CatControl.Tx?.IsRunning == true);
      UplinkBaseValue.ForeColor = satellite && link.HasUplink ? txColor : Color.Gray;

      TuningFrequencyLabel.Text = $"{link.CorrectedDownlinkFrequency:n0} Hz";
      TuningFrequencyLabel.ForeColor = rxColor;
      TuningBar.ForeColor = rxColor;
      TuningBar.SetFrequency(link.CorrectedDownlinkFrequency);

      if (link.RitEnabled)
        TuningModeLabel.Text = $"Tuning target: RIT ({link.RitOffset:+0;-0;0} Hz)";
      else if (link.IsTerrestrial)
        TuningModeLabel.Text = "Tuning target: terrestrial frequency";
      else if (link.IsTransponder)
        TuningModeLabel.Text = $"Tuning target: transponder offset ({link.TransponderOffset:n0} Hz)";
      else
        TuningModeLabel.Text = $"Tuning target: Manual correction ({link.DownlinkManualCorrection:+0;-0;0} Hz)";
    }

    private static Color GetFrequencyColor(double frequency, bool bright)
    {
      if (SatnogsDbTransmitter.IsUhfFrequency(frequency))
        return bright ? Color.Cyan : Color.Teal;
      if (SatnogsDbTransmitter.IsVhfFrequency(frequency))
        return bright ? Color.Yellow : Color.Olive;
      return bright ? Color.White : Color.Gray;
    }

    private void EditBaseFrequency(bool uplink)
    {
      if (ctx == null) return;
      var link = ctx.FrequencyControl.RadioLink;
      if (link.IsTerrestrial || link.TxCust == null || (uplink && !link.HasUplink)) return;

      double frequency = uplink ? link.BaseUplinkFrequency : link.BaseDownlinkFrequency;
      FrequencyDialog.Location = Cursor.Position;
      FrequencyDialog.SetInitialFrequency(
        frequency,
        uplink ? "Edit Uplink Base Frequency" : "Edit Downlink Base Frequency");

      FrequencyDialog.ShowDialog(this);
      if (FrequencyDialog.EnteredFrequency <= 0) return;

      if (uplink)
        ctx.FrequencyControl.SetUplinkBaseFrequency(FrequencyDialog.EnteredFrequency);
      else
        ctx.FrequencyControl.SetDownlinkBaseFrequency(FrequencyDialog.EnteredFrequency);
    }

    private void ResetBaseFrequency(bool uplink)
    {
      if (ctx == null) return;

      if (uplink)
        ctx.FrequencyControl.ResetUplinkBaseFrequency();
      else
        ctx.FrequencyControl.ResetDownlinkBaseFrequency();
    }

    private void TuningBar_TuneDeltaRequested(int delta)
    {
      ctx?.FrequencyControl.IncrementDownlinkFrequency(delta);
    }

    private void FrequencyControlPanel_FormClosing(object? sender, FormClosingEventArgs e)
    {
      if (ctx == null) return;

      Log.Information("Closing FrequencyControlPanel");
      ctx.FrequencyControlPanel = null;
      ctx.MainForm.FrequencyControlMNU.Checked = false;
    }
  }
}
