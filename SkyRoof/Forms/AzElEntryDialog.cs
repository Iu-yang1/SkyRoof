using VE3NEA;

namespace SkyRoof
{
  public partial class AzElEntryDialog : Form
  {
    private Context ctx;

    public AzElEntryDialog()
    {
      InitializeComponent();
    }

    public void Open(Context ctx)
    {
      this.ctx = ctx;
      SetupSpinners();
      Location = Cursor.Position;
      ShowDialog();
    }

    private void OkBtn_Click(object sender, EventArgs e)
    {
      StartRotation();
      Close();
    }

    private void ParkBtn_Click(object sender, EventArgs e)
    {
      ctx.RotatorControl.ManualPark();
      Close();
    }

    private void SetupSpinners()
    {
      AzimuthSpinner.Minimum = ctx.Settings.Rotator.MinAzimuth;
      AzimuthSpinner.Maximum = ctx.Settings.Rotator.MaxAzimuth;
      ElevationSpinner.Minimum = ctx.Settings.Rotator.MinElevation;
      ElevationSpinner.Maximum = ctx.Settings.Rotator.MaxElevation;

      bool manualEnabled =
        !ctx.RotatorControl.IsManualControlLocked;

      AzimuthSpinner.Enabled = manualEnabled;
      ElevationSpinner.Enabled = manualEnabled;
      OkBtn.Enabled = manualEnabled;
      ParkBtn.Enabled = manualEnabled;

      Text =
        manualEnabled
          ? "Manual Rotator Control"
          : "Manual Rotator Control — locked during live tracking";
    }

    private void StartRotation()
    {
      ctx.RotatorControl.ManualMoveToDegrees(
        (double)AzimuthSpinner.Value,
        (double)ElevationSpinner.Value);
    }
  }
}
