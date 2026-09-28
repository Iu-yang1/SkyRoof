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
    private MoonEphemeris? MoonFileEphemeris;

    private bool IsMoonTarget =>
      ctx?.Settings.OrbitSources.RotatorTarget == RotatorTrackingTarget.Moon;

    // set while auto-selection programmatically engages tracking for a specific pass, so the checkbox
    // handler keeps that exact pass instead of rebuilding the path from GetNextPass
    private bool settingTrack;
    public Bearing? AntBearing { get => engine?.LastReadBearing; }

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

      ReloadOrbitSources();

      if (IsMoonTarget)
      {
        Path = null;
        ResetUi();
      }
      else
      {
        SetSatellite(ctx.SatelliteSelector.SelectedSatellite);
      }

      TrackCheckbox.Checked = track;
      Advance();

      ctx.MainForm.ShowRotatorStatus();
    }

    public void SetSatellite(SatnogsDbSatellite? sat)
    {
      if (IsMoonTarget) return;
      if (sat == Path?.Satellite) return;

      engine?.StopRotation();

      if (sat == null)
        Path = null;
      else
        SetPass(ctx.HamPasses.GetNextPass(sat));
    }

    public void SetPass(SatellitePass? pass)
    {
      if (IsMoonTarget) return;

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
      toolTip1.SetToolTip(TrackCheckbox, $"Track {pass?.Satellite?.name} orbit {pass?.OrbitNumber}");
    }

    internal void Advance()
    {
      if (IsMoonTarget)
      {
        AdvanceMoon();
        return;
      }

      if (Path == null) return;

      SatBearing = Path.GetSatelliteBearing()?.Normalize();
      if (SatBearing == null) StopRotation();

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

    private void AdvanceMoon()
    {
      SatBearing = GetMoonBearing(DateTime.UtcNow)?.Normalize();

      if (SatBearing == null)
      {
        if (TrackCheckbox.Checked) StopRotation();
        BearingToUi();
        return;
      }

      BearingToUi();
      ctx.Announcer.AnnouncePosition(SatBearing);

      if (engine == null || !TrackCheckbox.Checked)
        return;

      double maxError =
        0.5 * ctx.Settings.Rotator.StepSize * Geo.RinD;
      Bearing requested = Sanitize(SatBearing);

      if (AntBearing == null ||
          AngleBetween(requested, AntBearing) >= maxError)
        RotateTo(requested);
    }

    private Bearing? GetMoonBearing(DateTime utc)
    {
      Bearing? imported = MoonFileEphemeris?.GetBearing(utc);
      if (imported != null) return imported;

      if (!ctx.Settings.OrbitSources.UseBuiltInMoonFallback)
        return null;

      GeoPoint observer =
        GridSquare.ToGeoPoint(ctx.Settings.User.Square);

      return MoonEphemeris.GetBuiltInMoonBearing(
        utc,
        observer,
        ctx.Settings.User.Altitude);
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
      if (IsMoonTarget || engine == null || pass == null) return;

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
      TrackCheckbox.Checked = !TrackCheckbox.Checked;
      TrackCheckbox_CheckedChanged(StopBtn, EventArgs.Empty);
    }

    public string? GetStatusString()
    {
      if (!ctx.Settings.Rotator.Enabled) return "Rotator control disabled";
      else if (!IsRunning()) return "No connection";
      else if (!TrackCheckbox.Checked) return "Connected, tracking disabled";
      else if (IsMoonTarget) return "Connected and tracking Moon / EME target";
      else return "Connected and tracking selected satellite";
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
        if (IsMoonTarget)
        {
          RotateTo(GetMoonBearing(DateTime.UtcNow));
        }
        // auto-selection already set the exact pass in TrackPass; only rebuild for a manual check
        else if (!settingTrack && Path != null)
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

    private void TargetBtn_Click(object sender, EventArgs e)
    {
      var menu = new ContextMenuStrip();

      var satelliteItem = new ToolStripMenuItem("Selected Satellite")
      {
        Checked = !IsMoonTarget
      };
      satelliteItem.Click += (_, _) => SelectSatelliteTarget();

      var moonItem = new ToolStripMenuItem("Moon / EME")
      {
        Checked = IsMoonTarget
      };
      moonItem.Click += (_, _) => SelectMoonTarget();

      var loadItem = new ToolStripMenuItem("Load Moon Ephemeris CSV...");
      loadItem.Click += (_, _) => LoadMoonEphemerisFile();

      menu.Items.Add(satelliteItem);
      menu.Items.Add(moonItem);
      menu.Items.Add(new ToolStripSeparator());
      menu.Items.Add(loadItem);
      menu.Closed += (_, _) => menu.Dispose();
      menu.Show(
        TargetBtn,
        new Point(0, TargetBtn.Height),
        ToolStripDropDownDirection.BelowRight);
    }

    internal void ReloadOrbitSources()
    {
      MoonFileEphemeris = MoonEphemeris.TryLoad(
        ctx.Settings.OrbitSources.MoonEphemerisFile);

      TargetBtn.Text = IsMoonTarget ? "MOON" : "SAT";
      toolTip1.SetToolTip(
        TargetBtn,
        IsMoonTarget
          ? "Tracking target: Moon / EME"
          : "Tracking target: selected satellite");
    }

    private void SelectSatelliteTarget()
    {
      if (!IsMoonTarget) return;

      StopRotation();
      ctx.Settings.OrbitSources.RotatorTarget =
        RotatorTrackingTarget.Satellite;
      ctx.Settings.SaveToFile();

      Path = null;
      ReloadOrbitSources();
      SetSatellite(ctx.SatelliteSelector.SelectedSatellite);
      ResetUi();
      Advance();
      ctx.MainForm.ShowRotatorStatus();
    }

    private void SelectMoonTarget()
    {
      if (IsMoonTarget) return;

      StopRotation();
      ctx.Settings.OrbitSources.RotatorTarget =
        RotatorTrackingTarget.Moon;
      ctx.Settings.SaveToFile();

      Path = null;
      engine?.StopRotation();
      ReloadOrbitSources();
      ResetUi();
      Advance();
      toolTip1.SetToolTip(
        TrackCheckbox,
        "Track Moon using imported ephemeris or built-in topocentric lunar position");
      ctx.MainForm.ShowRotatorStatus();
    }

    private void LoadMoonEphemerisFile()
    {
      using var dlg = new OpenFileDialog
      {
        Filter =
          "Ephemeris CSV/Text (*.csv;*.txt)|*.csv;*.txt|All Files (*.*)|*.*",
        Title = "Load Moon / EME Observer Ephemeris"
      };

      if (!string.IsNullOrWhiteSpace(
            ctx.Settings.OrbitSources.MoonEphemerisFile))
        dlg.FileName =
          ctx.Settings.OrbitSources.MoonEphemerisFile;

      if (dlg.ShowDialog(this) != DialogResult.OK)
        return;

      ctx.Settings.OrbitSources.MoonEphemerisFile = dlg.FileName;
      ctx.Settings.OrbitSources.RotatorTarget =
        RotatorTrackingTarget.Moon;
      ctx.Settings.SaveToFile();

      StopRotation();
      Path = null;
      ReloadOrbitSources();
      ResetUi();
      Advance();
      ctx.MainForm.ShowRotatorStatus();
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
      TrackCheckbox.Enabled =
        ctx.Settings.Rotator.Enabled &&
        (IsMoonTarget || Path != null);

      TargetBtn.Text = IsMoonTarget ? "MOON" : "SAT";
    }

    private void BearingToUi()
    {
      var realSatBearing =
        IsMoonTarget
          ? SatBearing
          : Path?.GetRealSatelliteBearing();

      if (realSatBearing == null || SatBearing == null)
      {
        SatelliteAzimuthLabel.ForeColor = Color.Gray;
        SatelliteElevationLabel.ForeColor = Color.Gray;
        SatelliteAzimuthLabel.Text = "---";
        SatelliteElevationLabel.Text = "---";
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