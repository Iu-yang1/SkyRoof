using System.Drawing;
using VE3NEA;

namespace SkyRoof
{
  // Right-click PARK edits a persisted, ordered list of mechanical az/el targets.
  // The editor only changes settings; starting motion still requires a left click.
  internal sealed class ParkPositionDialog : Form
  {
    private readonly Context ctx;
    private readonly Bearing? actualBearing;
    private readonly List<RotatorParkWaypoint> waypoints;
    private readonly ListBox RouteList = new();
    private readonly NumericUpDown AzimuthSpinner = new();
    private readonly NumericUpDown ElevationSpinner = new();

    internal ParkPositionDialog(Context context, Bearing? actual)
    {
      ctx = context;
      actualBearing = actual;
      var settings = ctx.Settings.Rotator;
      waypoints = settings.ParkWaypoints?.Count > 0
        ? settings.ParkWaypoints.Select(w => new RotatorParkWaypoint
          { Azimuth = w.Azimuth, Elevation = w.Elevation }).ToList()
        : new List<RotatorParkWaypoint> {
            new() { Azimuth = settings.ParkAzimuth, Elevation = settings.ParkElevation } };

      Text = "PARK Waypoints";
      FormBorderStyle = FormBorderStyle.FixedDialog;
      // Center over the main SkyRoof window rather than at the mouse cursor.
      StartPosition = FormStartPosition.CenterParent;
      ShowInTaskbar = false;
      MinimizeBox = false;
      MaximizeBox = false;
      AutoScaleMode = AutoScaleMode.Font;
      ClientSize = new Size(490, 345);

      Controls.Add(new Label {
        Location = new Point(12, 10),
        Size = new Size(462, 36),
        Text = "PARK follows the ordered waypoints. The next move starts only after " +
               "the rotator confirms arrival. STOP cancels the route."
      });

      RouteList.Location = new Point(12, 52);
      RouteList.Size = new Size(258, 208);
      RouteList.SelectedIndexChanged += (_, _) => LoadSelected();
      Controls.Add(RouteList);

      Controls.Add(new Label {
        Location = new Point(284, 55), Size = new Size(190, 20),
        Text = "Selected waypoint coordinates"
      });
      Controls.Add(new Label {
        Location = new Point(284, 85), Size = new Size(34, 23), Text = "AZ"
      });
      ConfigureSpinner(AzimuthSpinner,
        settings.MinAzimuth, settings.MaxAzimuth, settings.ParkAzimuth);
      AzimuthSpinner.Location = new Point(320, 82);
      AzimuthSpinner.Size = new Size(128, 24);
      Controls.Add(AzimuthSpinner);

      Controls.Add(new Label {
        Location = new Point(284, 117), Size = new Size(34, 23), Text = "EL"
      });
      ConfigureSpinner(ElevationSpinner,
        settings.MinElevation, settings.MaxElevation, settings.ParkElevation);
      ElevationSpinner.Location = new Point(320, 114);
      ElevationSpinner.Size = new Size(128, 24);
      Controls.Add(ElevationSpinner);

      AddButton("Update", 284, 149, 80, (_, _) => UpdateSelected());
      AddButton("Add After", 370, 149, 94, (_, _) => AddWaypoint());
      AddButton("Remove", 284, 185, 80, (_, _) => RemoveSelected());
      AddButton("↑ Up", 370, 185, 94, (_, _) => MoveWaypoint(-1));
      AddButton("Use Current", 284, 221, 180, (_, _) => UseCurrent())
        .Enabled = actualBearing != null;
      AddButton("↓ Down", 370, 260, 94, (_, _) => MoveWaypoint(1));

      Controls.Add(new Label {
        Location = new Point(12, 265),
        Size = new Size(258, 38),
        Text = "Use Update to save edited AZ/EL into the selected row; " +
               "Save persists the entire route."
      });

      Button save = AddButton("Save Route", 284, 302, 90, (_, _) => SaveRoute());
      Button cancel = AddButton("Cancel", 382, 302, 82,
        (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
      CancelButton = cancel;
      AcceptButton = save;

      RefreshRoute(0);
      Shown += (_, _) =>
      {
        // CenterParent resolves after ShowDialog(owner) starts. Clamp the
        // resulting bounds *entirely* into the owner's monitor working area,
        // including when the main window straddles displays or is near an edge.
        Rectangle workingArea = Screen.FromControl(ctx.MainForm).WorkingArea;
        Bounds = ClampToWorkingArea(Bounds, workingArea);
      };
    }

    internal static Rectangle ClampToWorkingArea(
      Rectangle windowBounds,
      Rectangle workingArea)
    {
      int width = Math.Min(windowBounds.Width, workingArea.Width);
      int height = Math.Min(windowBounds.Height, workingArea.Height);

      return new Rectangle(
        Math.Clamp(windowBounds.Left,
          workingArea.Left, workingArea.Right - width),
        Math.Clamp(windowBounds.Top,
          workingArea.Top, workingArea.Bottom - height),
        width,
        height);
    }

    private Button AddButton(
      string text, int x, int y, int width, EventHandler onClick)
    {
      var button = new Button {
        Text = text, Location = new Point(x, y), Size = new Size(width, 28)
      };
      button.Click += onClick;
      Controls.Add(button);
      return button;
    }

    private static void ConfigureSpinner(
      NumericUpDown spinner, double min, double max, double value)
    {
      spinner.DecimalPlaces = 1;
      spinner.Increment = 0.5M;
      spinner.Minimum = (decimal)Math.Min(min, max);
      spinner.Maximum = (decimal)Math.Max(min, max);
      spinner.Value = Math.Clamp(
        (decimal)value, spinner.Minimum, spinner.Maximum);
    }

    private void RefreshRoute(int selectedIndex)
    {
      RouteList.BeginUpdate();
      RouteList.Items.Clear();
      for (int i = 0; i < waypoints.Count; i++)
      {
        RotatorParkWaypoint w = waypoints[i];
        RouteList.Items.Add(
          $"PARK {i + 1}:  AZ {w.Azimuth:0.0}°   EL {w.Elevation:0.0}°");
      }
      RouteList.EndUpdate();
      if (waypoints.Count > 0)
        RouteList.SelectedIndex = Math.Clamp(selectedIndex, 0, waypoints.Count - 1);
    }

    private void LoadSelected()
    {
      int i = RouteList.SelectedIndex;
      if (i < 0 || i >= waypoints.Count) return;
      AzimuthSpinner.Value = Math.Clamp(
        (decimal)waypoints[i].Azimuth,
        AzimuthSpinner.Minimum, AzimuthSpinner.Maximum);
      ElevationSpinner.Value = Math.Clamp(
        (decimal)waypoints[i].Elevation,
        ElevationSpinner.Minimum, ElevationSpinner.Maximum);
    }

    private RotatorParkWaypoint ReadEditor() => new() {
      Azimuth = (float)AzimuthSpinner.Value,
      Elevation = (float)ElevationSpinner.Value
    };

    private void UpdateSelected()
    {
      int i = RouteList.SelectedIndex;
      if (i < 0 || i >= waypoints.Count) return;
      waypoints[i] = ReadEditor();
      RefreshRoute(i);
    }

    private void AddWaypoint()
    {
      int i = RouteList.SelectedIndex < 0 ? waypoints.Count :
        RouteList.SelectedIndex + 1;
      waypoints.Insert(i, ReadEditor());
      RefreshRoute(i);
    }

    private void RemoveSelected()
    {
      int i = RouteList.SelectedIndex;
      if (i < 0 || i >= waypoints.Count) return;
      waypoints.RemoveAt(i);
      RefreshRoute(Math.Max(0, i - 1));
    }

    private void MoveWaypoint(int offset)
    {
      int i = RouteList.SelectedIndex;
      int j = i + offset;
      if (i < 0 || j < 0 || j >= waypoints.Count) return;
      (waypoints[i], waypoints[j]) = (waypoints[j], waypoints[i]);
      RefreshRoute(j);
    }

    private void UseCurrent()
    {
      if (actualBearing == null) return;
      AzimuthSpinner.Value = Math.Clamp(
        (decimal)actualBearing.AzDeg,
        AzimuthSpinner.Minimum, AzimuthSpinner.Maximum);
      ElevationSpinner.Value = Math.Clamp(
        (decimal)actualBearing.ElDeg,
        ElevationSpinner.Minimum, ElevationSpinner.Maximum);
    }

    private void SaveRoute()
    {
      // Saving the dialog should also commit the currently edited row;
      // the operator need not remember to press Update first.
      if (RouteList.SelectedIndex >= 0)
        UpdateSelected();

      if (waypoints.Count == 0)
      {
        MessageBox.Show(this,
          "Add at least one PARK waypoint.",
          "PARK Waypoints", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      // The last waypoint is the final parked orientation and is also kept
      // in the old fields so older configuration readers remain compatible.
      ctx.Settings.Rotator.ParkWaypoints = waypoints
        .Select(w => new RotatorParkWaypoint {
          Azimuth = w.Azimuth, Elevation = w.Elevation }).ToList();
      var last = waypoints[^1];
      ctx.Settings.Rotator.ParkAzimuth = last.Azimuth;
      ctx.Settings.Rotator.ParkElevation = last.Elevation;
      ctx.Settings.SaveToFile();
      DialogResult = DialogResult.OK;
      Close();
    }
  }
}
