using VE3NEA;

namespace SkyRoof
{
  // Configuration-only dialog for the PARK target. Saving a preset never
  // moves the rotator; left-clicking PARK remains the explicit movement action.
  internal sealed class ParkPositionDialog : Form
  {
    private readonly Context ctx;
    private readonly Bearing? actualBearing;
    private readonly NumericUpDown AzimuthSpinner = new();
    private readonly NumericUpDown ElevationSpinner = new();
    private readonly Button CurrentButton = new();
    private readonly Button SaveButton = new();
    private readonly Button CancelBtn = new();

    internal ParkPositionDialog(
      Context context,
      Bearing? actual)
    {
      ctx = context;
      actualBearing = actual;

      Text = "Park Position";
      FormBorderStyle = FormBorderStyle.FixedToolWindow;
      StartPosition = FormStartPosition.Manual;
      ShowInTaskbar = false;
      MinimizeBox = false;
      MaximizeBox = false;
      ClientSize = new Size(286, 142);
      AutoScaleMode = AutoScaleMode.Font;

      var description = new Label
      {
        AutoSize = false,
        Location = new Point(12, 10),
        Size = new Size(262, 35),
        Text = "Save the PARK azimuth and elevation.\nThis does not move the rotator.",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(description);

      var azLabel = new Label
      {
        AutoSize = false,
        Location = new Point(12, 51),
        Size = new Size(32, 24),
        Text = "AZ",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(azLabel);

      ConfigureSpinner(
        AzimuthSpinner,
        ctx.Settings.Rotator.MinAzimuth,
        ctx.Settings.Rotator.MaxAzimuth,
        ctx.Settings.Rotator.ParkAzimuth);
      AzimuthSpinner.Location = new Point(44, 51);
      AzimuthSpinner.Size = new Size(78, 24);
      Controls.Add(AzimuthSpinner);

      var elLabel = new Label
      {
        AutoSize = false,
        Location = new Point(137, 51),
        Size = new Size(30, 24),
        Text = "EL",
        TextAlign = ContentAlignment.MiddleLeft
      };
      Controls.Add(elLabel);

      ConfigureSpinner(
        ElevationSpinner,
        ctx.Settings.Rotator.MinElevation,
        ctx.Settings.Rotator.MaxElevation,
        ctx.Settings.Rotator.ParkElevation);
      ElevationSpinner.Location = new Point(167, 51);
      ElevationSpinner.Size = new Size(78, 24);
      Controls.Add(ElevationSpinner);

      CurrentButton.Location = new Point(12, 91);
      CurrentButton.Size = new Size(90, 28);
      CurrentButton.Text = "Use Current";
      CurrentButton.Enabled = actualBearing != null;
      CurrentButton.Click += CurrentButton_Click;
      Controls.Add(CurrentButton);

      SaveButton.Location = new Point(111, 91);
      SaveButton.Size = new Size(75, 28);
      SaveButton.Text = "Save";
      SaveButton.Click += SaveButton_Click;
      Controls.Add(SaveButton);

      CancelBtn.Location = new Point(195, 91);
      CancelBtn.Size = new Size(75, 28);
      CancelBtn.Text = "Cancel";
      CancelBtn.DialogResult = DialogResult.Cancel;
      Controls.Add(CancelBtn);

      AcceptButton = SaveButton;
      CancelButton = CancelBtn;

      Location = Cursor.Position;
      Shown += (_, _) => Utils.EnsureFormVisible(this);
    }

    private static void ConfigureSpinner(
      NumericUpDown spinner,
      double min,
      double max,
      double value)
    {
      if (max < min)
        (min, max) = (max, min);

      spinner.DecimalPlaces = 1;
      spinner.Increment = 0.5M;
      spinner.Minimum = (decimal)min;
      spinner.Maximum = (decimal)max;
      spinner.Value =
        Math.Clamp(
          (decimal)value,
          spinner.Minimum,
          spinner.Maximum);
    }

    private void CurrentButton_Click(
      object? sender,
      EventArgs e)
    {
      if (actualBearing == null)
        return;

      AzimuthSpinner.Value =
        Math.Clamp(
          (decimal)actualBearing.AzDeg,
          AzimuthSpinner.Minimum,
          AzimuthSpinner.Maximum);

      ElevationSpinner.Value =
        Math.Clamp(
          (decimal)actualBearing.ElDeg,
          ElevationSpinner.Minimum,
          ElevationSpinner.Maximum);
    }

    private void SaveButton_Click(
      object? sender,
      EventArgs e)
    {
      var target =
        RotatorWidget.ClampManualTarget(
          (double)AzimuthSpinner.Value,
          (double)ElevationSpinner.Value,
          ctx.Settings.Rotator);

      ctx.Settings.Rotator.ParkAzimuth =
        (float)target.AzimuthDeg;
      ctx.Settings.Rotator.ParkElevation =
        (float)target.ElevationDeg;
      ctx.Settings.SaveToFile();

      DialogResult = DialogResult.OK;
      Close();
    }
  }
}
