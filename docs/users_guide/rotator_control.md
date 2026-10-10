# Rotator Control

The Rotator Control panel on the status bar shows the current position of the selected satellite
and the antenna bearing, if the rotator control function is enabled:

![Rotator Control](../images/rotator_control.png)

## Display

The large Azimuth and Elevation display shows the satellite location, the small numbers below it
show the antenna bearing.

The satellite location is dimmed when the rotator control function is disabled. Click on **Rotator**
on the status bar to enable or disable this function.

When rotator control is enabled, the current antenna bearing is marked on the
[Sky View panel](sky_view_panel.md) with a red spot:

![Red Spot](../images/red_spot.png)

## Tracking

When rotator control is enabled but the **Track** checkbox is not ticked, the panel only displays the antenna
bearing but does not attempt to change it. Tick the **Track** checkbox to start tracking. Note that
the check box is cleared when you switch to another satellite.

In the satellite tracking mode, the antenna bearing turns pink if it differs from the satellite position
by more than 1.5 the **Step Size** setting entered in the
[rotator settings](setting_up_rotator_control.md).

## Manual Control

Click on the satellite position display, or right-click the rotator/status area,
to open the **Manual Rotator Control** window:

![Manual Rotator Control](../images/manual_rotator_control.png)

The direction wheel now uses **continuous rotctld movement**, not repeated
absolute position steps:

- press and hold **◀ / ▶ / ▲ / ▼** to start continuous LEFT / RIGHT / UP /
  DOWN movement;
- release the mouse button (including capture loss outside the wheel) to send
  **STOP** immediately;
- the center **STOP** button also stops movement;
- click **Go** to rotate to the explicitly entered azimuth/elevation;
- click **Park** to follow the configured PARK route.

For Hamlib this is the rotctld `M direction -1` command followed by `S` on
release. The `-1` speed value preserves the backend/controller's current
speed.

### Track step

The **Track step** control beside the wheel is **not** a manual-jog distance.
It is the same automatic tracking `StepSize` used by the path optimizer and
tracking error threshold. Changing it in the popup is saved immediately and
the current pass path is rebuilt, so the new value takes effect without
waiting for a satellite/pass change.

A smaller value causes SkyRoof to issue tracking position updates for smaller
angular errors; a larger value reduces how often tracking targets are updated.

## Stopping

To stop antenna rotation, either manual or due to the satellite tracking, click on the **Stop** button.

## See Also

- [Smart Antenna Rotation](smart_antenna_rotation.md)
- [Auto Selection Panel](auto_selection_panel.md) — tracking the antenna automatically on scheduled passes