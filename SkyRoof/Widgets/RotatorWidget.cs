using SkyRoof;
using VE3NEA;

namespace SkyRoof
{

  public partial class RotatorWidget : UserControl
  {
    public Context ctx;
    private RotatorControlEngine? engine;
    private AzElEntryDialog Dialog = new();
    private OptimizedRotationPath? Path;
    private Bearing? SatBearing;

    // set while auto-selection programmatically engages tracking for a specific pass, so the checkbox
    // handler keeps that exact pass instead of rebuilding the path from GetNextPass
    private bool settingTrack;
    public Bearing? AntBearing { get => engine?.LastReadBearing; }
    public Bearing? SatelliteBearing => SatBearing;

    // PathOptimizerForm instance is created once and reused
    private PathOptimizerForm dialog = new();

    public RotatorWidget()
    {
      InitializeComponent();

      // Create the PathOptimizerForm on startup, but do not show it yet
      dialog.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; dialog.Hide(); };
    }

    //----------------------------------------------------------------------------------------------
    //                               public interface
    //----------------------------------------------------------------------------------------------
    public void ApplySettings(bool restoreTracking = false)
    {
      bool track = restoreTracking && TrackCheckbox.Checked;

      if (engine != null) StopRotation();
      engine?.Dispose();
      engine = null;

      if (ctx.Settings.Rotator.Enabled)
      {
        engine = new RotatorControlEngine(ctx.Settings.Rotator);
        engine.StatusChanged += Engine_StatusChanged;
        engine.BearingChanged += Engine_BearingChanged;
      }

      ResetUi();

      SetSatellite(ctx.SatelliteSelector.SelectedSatellite);

      TrackCheckbox.Checked = track;
      Advance();

      ctx.MainForm.ShowRotatorStatus();
    }

    public void SetSatellite(SatnogsDbSatellite? sat)
    {
      if (sat == Path?.Satellite) return;

      engine?.StopRotation();

      if (sat == null)
        Path = null;
      else
        SetPass(ctx.HamPasses.GetNextPass(sat));
    }

    public void SetPass(SatellitePass? pass)
    {
      // re-selecting the same pass must not disturb tracking (mirrors the SetSatellite guard); passes are
      // recomputed objects, so compare by identity (sat + orbit), not reference
      if (pass != null && Path?.Pass != null
        && pass.Satellite.sat_id == Path.Pass.Satellite.sat_id
        && pass.OrbitNumber == Path.Pass.OrbitNumber) return;

      Path = pass == null ? null : Path = new(pass, ctx.Settings.Rotator, AntBearing);

      ResetUi();
      Advance();
      // show black LED if no satellite
      ctx.MainForm.ShowRotatorStatus();

      UpdatePathOptimizerForm();
      toolTip1.SetToolTip(
        TrackCheckbox,
        pass == null
          ? "No tracking target"
          : pass.HasOrbitNumber
            ? $"Track {pass.Satellite.name} orbit {pass.OrbitNumber}"
            : $"Track {pass.Satellite.name} JPL ephemeris");
    }

    internal void Advance()
    {
      if (Path == null) return;

      SatBearing = Path.GetSatelliteBearing()?.Normalize();
      // A missing/expired pass ends *automatic* tracking only. If Track is
      // unchecked, the operator may be moving the rotator manually and a
      // one-second tick must not issue an unsolicited STOP.
      if (SatBearing == null && TrackCheckbox.Checked) StopRotation();

      BearingToUi();
      ctx.Announcer.AnnouncePosition(SatBearing);

      if (SatBearing != null && engine != null && TrackCheckbox.Checked)
      {
        var maxError = 0.5 * ctx.Settings.Rotator.StepSize * Geo.RinD;
        var bearing = Sanitize(SatBearing);
        if (AntBearing == null || AngleBetween(bearing, AntBearing) >= maxError)
          RotateTo(Path.GetNextAntennaBearing());
      }
    }

    public void Retry()
    {
      engine?.Retry();
    }

    public bool IsRunning()
    {
      return engine != null && engine.IsRunning;
    }

    // true when the rotator is actively tracking (the track box is on and a rotator is present); the
    // single source of truth used by auto-selection instead of a shadow flag
    public bool IsTracking => engine != null && TrackCheckbox.Checked;

    // Manual control is locked only after AOS while Track remains enabled.
    // Pre-positioning before AOS may still be adjusted manually; the first
    // manual command will explicitly clear Track and take ownership.
    public bool IsManualControlLocked =>
      ShouldLockManualControl(
        IsTracking,
        Path?.Pass?.IsActive() == true);

    internal static bool ShouldLockManualControl(
      bool isTracking,
      bool passIsActive) =>
      isTracking &&
      passIsActive;

    public void RotateTo(Bearing? bearing)
    {
      if (engine == null || bearing == null) return;

      var sanitizedBearing = Sanitize(bearing);
      engine.RotateTo(sanitizedBearing);
    }

    // engages tracking of a specific pass on behalf of auto-selection: sets the path to that exact pass
    // (not GetNextPass) and turns tracking on. a no-op when rotator control is disabled (engine == null),
    // so the schedule's tracking option is harmless while the rotator is off
    public void TrackPass(SatellitePass? pass)
    {
      if (engine == null || pass == null) return;

      Path = new(pass, ctx.Settings.Rotator, AntBearing);
      TrackCheckbox.Enabled = true;

      // check the box without letting the handler rebuild the path from GetNextPass; then start moving
      settingTrack = true;
      try { TrackCheckbox.Checked = true; }
      finally { settingTrack = false; }

      RotateTo(Path?.GetNextAntennaBearing());
      BearingToUi();
      UpdatePathOptimizerForm();
      ctx.MainForm.ShowRotatorStatus();
      toolTip1.SetToolTip(TrackCheckbox, $"Auto track {pass.Satellite.name} orbit {pass.OrbitNumber}");
    }

    public void StopRotation()
    {
      TrackCheckbox.Checked = false;
      engine?.StopRotation();
    }

    public void ToggleTracking()
    {
      if (!TrackCheckbox.Enabled) return;
      // CheckedChanged is raised synchronously by Checked; invoking its handler
      // again duplicates tracking-path recomputation and stop requests.
      TrackCheckbox.Checked = !TrackCheckbox.Checked;
    }

    internal Bearing? GetManualActualBearing()
    {
      return AntBearing == null
        ? null
        : Unsanitize(AntBearing);
    }

    internal Bearing? GetManualTargetBearing()
    {
      Bearing? target = engine?.RequestedBearing;
      return target == null
        ? null
        : Unsanitize(target);
    }

    internal void ManualMoveToDegrees(
      double azimuthDeg,
      double elevationDeg)
    {
      if (engine == null || IsManualControlLocked) return;

      var target =
        ClampManualTarget(
          azimuthDeg,
          elevationDeg,
          ctx.Settings.Rotator);

      // Before AOS, Track may already be checked because the antenna is
      // pre-positioning for the upcoming pass. Manual control is allowed in
      // that state, but the operator must explicitly take ownership first.
      if (TrackCheckbox.Checked)
        TrackCheckbox.Checked = false;

      RotateTo(
        new Bearing(
          target.AzimuthDeg * Trig.RinD,
          target.ElevationDeg * Trig.RinD));
    }

    internal void ManualJog(
      double azimuthDeltaDeg,
      double elevationDeltaDeg)
    {
      if (engine == null || IsManualControlLocked) return;

      // The first jog while tracking starts at the actual antenna position,
      // not at a possibly far-ahead tracking target. Subsequent repeated jogs
      // build on the last manual command so press-and-hold feels continuous
      // even while the physical rotator is still catching up.
      Bearing? basis =
        TrackCheckbox.Checked
          ? GetManualActualBearing() ??
            GetManualTargetBearing()
          : GetManualTargetBearing() ??
            GetManualActualBearing();

      if (basis == null) return;

      ManualMoveToDegrees(
        basis.AzDeg + azimuthDeltaDeg,
        basis.ElDeg + elevationDeltaDeg);
    }

    internal void ManualPark()
    {
      if (IsManualControlLocked) return;

      ManualMoveToDegrees(
        ctx.Settings.Rotator.ParkAzimuth,
        ctx.Settings.Rotator.ParkElevation);
    }

    internal static (
      double AzimuthDeg,
      double ElevationDeg)
      ClampManualTarget(
        double azimuthDeg,
        double elevationDeg,
        RotatorSettings settings)
    {
      double minAz =
        Math.Min(
          settings.MinAzimuth,
          settings.MaxAzimuth);
      double maxAz =
        Math.Max(
          settings.MinAzimuth,
          settings.MaxAzimuth);
      double minEl =
        Math.Min(
          settings.MinElevation,
          settings.MaxElevation);
      double maxEl =
        Math.Max(
          settings.MinElevation,
          settings.MaxElevation);

      return (
        Math.Clamp(
          azimuthDeg,
          minAz,
          maxAz),
        Math.Clamp(
          elevationDeg,
          minEl,
          maxEl));
    }

    public string? GetStatusString()
    {
      if (!ctx.Settings.Rotator.Enabled) return "Rotator control disabled";
      else if (!IsRunning()) return "No connection";
      else if (!TrackCheckbox.Checked) return "Connected, tracking disabled";
      else return "Connected and tracking";
    }

    //----------------------------------------------------------------------------------------------
    //                                        UI
    //----------------------------------------------------------------------------------------------
    private void AzEl_Click(object sender, EventArgs e)
    {
      if (ModifierKeys == (Keys.Control | Keys.Shift))  ShowRotatorDebugInfo();
      else Dialog.Open(ctx);
    }

    private void TrackCheckbox_CheckedChanged(object sender, EventArgs e)
    {

      if (TrackCheckbox.Checked)
      {
        // auto-selection already set the exact pass in TrackPass; only rebuild for a manual check
        if (!settingTrack && Path != null)
        {
          // re-optimize the path from the current antenna position, but keep the pass we already have while
          // it is still live: it is the exact pass the operator is tracking, and re-deriving it costs a full
          // prediction. only look one up when the current pass is over, so pre-positioning for the next pass
          // between passes still works. no grace period here: once the pass has ended the antenna should be
          // pre-positioning for the next one, not still pointing at where the satellite set
          var pass = Path.Pass != null && DateTime.UtcNow < Path.Pass.EndTime
            ? Path.Pass
            : ctx.HamPasses.GetCurrentOrNextPass(Path!.Satellite);
          var sett = ctx.Settings.Rotator;
          Path = new(pass, sett, AntBearing);
          UpdatePathOptimizerForm();
          RotateTo(Path?.GetNextAntennaBearing());
        }
      }
      else
        StopRotation();

      // update color
      BearingToUi();

      ctx.MainForm.ShowRotatorStatus();
    }

    private void StopBtn_Click(object sender, EventArgs e)
    {
      StopRotation();
    }

    private void ResetUi()
    {
      SatelliteAzimuthLabel.ForeColor = Color.Gray;
      SatelliteElevationLabel.ForeColor = Color.Gray;

      SatelliteAzimuthLabel.Text = "0°";
      SatelliteElevationLabel.Text = "0°";
      AntennaAzimuthLabel.Text = "---";
      AntennaElevationLabel.Text = "---";

      TrackCheckbox.Checked = false;
      TrackCheckbox.Enabled = ctx.Settings.Rotator.Enabled && Path != null;
    }

    private void BearingToUi()
    {
      var realSatBearing = Path?.GetRealSatelliteBearing();
      if (realSatBearing == null || SatBearing == null)
      {
        // Losing a displayed observation is not a user request to stop tracking.
        // ResetUi() would uncheck Track and queue an S command, so limit this
        // branch to rendering. Advance() owns the decision to stop at LOS.
        SatelliteAzimuthLabel.ForeColor = Color.Gray;
        SatelliteElevationLabel.ForeColor = Color.Gray;
        SatelliteAzimuthLabel.Text = "---";
        SatelliteElevationLabel.Text = "---";
        AntennaAzimuthLabel.BackColor = Color.Transparent;
        AntennaElevationLabel.BackColor = Color.Transparent;
        AntennaAzimuthLabel.Text = IsRunning() && AntBearing != null ? $"{AntBearing.AzDeg:F1}°" : "---";
        AntennaElevationLabel.Text = IsRunning() && AntBearing != null ? $"{AntBearing.ElDeg:F1}°" : "---";
        return;
      }

      Color satColor = TrackCheckbox.Checked ? Color.Aqua : Color.Teal;

      bool trackError = TrackCheckbox.Checked && (!IsRunning() ||
        AntBearing == null ||
        AngleBetween(SatBearing, AntBearing!) > 1.5 * ctx.Settings.Rotator.StepSize * Geo.RinD);

      Color antColor = trackError ? Color.LightCoral : Color.Transparent;

      SatelliteAzimuthLabel.ForeColor = satColor;
      SatelliteElevationLabel.ForeColor = satColor;
      SatelliteAzimuthLabel.Text = $"{realSatBearing.AzDeg:F0}°";
      SatelliteElevationLabel.Text = $"{realSatBearing.ElDeg:F0}°";

      AntennaAzimuthLabel.BackColor = antColor;
      AntennaElevationLabel.BackColor = antColor;

      if (IsRunning() && AntBearing != null)
      {
        AntennaAzimuthLabel.Text = $"{AntBearing.AzDeg:F1}°";
        AntennaElevationLabel.Text = $"{AntBearing.ElDeg:F1}°";
      }
      else
      {
        AntennaAzimuthLabel.Text = "---";
        AntennaElevationLabel.Text = "---";
      }
    }

    private void Engine_StatusChanged(object? sender, EventArgs e)
    {
      // ant bearing color
      BearingToUi();

      ctx.MainForm.ShowRotatorStatus();
    }

    private void Engine_BearingChanged(object? sender, EventArgs e)
    {
      BearingToUi();
      ctx.SkyViewPanel?.Refresh();
    }

    //----------------------------------------------------------------------------------------------
    //                                   helper functions
    //----------------------------------------------------------------------------------------------
    private Bearing Unsanitize(Bearing bearing)
    {
      var sett = ctx.Settings.Rotator;

      return new Bearing(
        bearing.Az -
          sett.AzimuthOffset * Trig.RinD,
        bearing.El -
          sett.ElevationOffset * Trig.RinD);
    }

    private Bearing Sanitize(Bearing bearing)
    {
      var sett = ctx.Settings.Rotator;

      var sanitizedBearing = new Bearing(bearing.Az, bearing.El);
      sanitizedBearing.Az += sett.AzimuthOffset * Trig.RinD;
      sanitizedBearing.El += sett.ElevationOffset * Trig.RinD;

      var bounds = new RectangleF(
        sett.MinAzimuth * Trig.RinD,
        sett.MinElevation * Trig.RinD,
        (sett.MaxAzimuth - sett.MinAzimuth) * Trig.RinD,
        (sett.MaxElevation - sett.MinElevation) * Trig.RinD
      );
      sanitizedBearing = sanitizedBearing.Clamp(bounds);

      return sanitizedBearing;
    }

    private double AngleBetween(Bearing bearing1, Bearing bearing2)
    {
      bool azOnly = ctx.Settings.Rotator.MinElevation == ctx.Settings.Rotator.MaxElevation;
      return bearing1.AngleFrom(bearing2, azOnly);
    }

    private void ShowRotatorDebugInfo()
    {
      UpdatePathOptimizerForm();

      if (!dialog.Visible) dialog.Show();
      else dialog.BringToFront();
    }
    private void UpdatePathOptimizerForm()
    {
      dialog.UpdateContents(Path);
    }
  }
}