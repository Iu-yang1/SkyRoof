# Rotator, PARK routes, interface themes and reliability

**Language:** English | [简体中文](../../zh-cn/fork-guide/rotator-and-ui.md)

## Hamlib rotctld rotator (PRs #22–23 and #25–28, #34)

Configure **Settings → Rotator Control** with the `rotctld` host and TCP port, normally **127.0.0.1:4533**; adjust azimuth/elevation limits, offsets and step size to the *real mechanical antenna and cable geometry*. A typical external rotator can be served by Hamlib with GS-232B-compatible hardware. The correct COM port, baud rate and protocol are configured in `rotctld`, not on SkyRoof's TCP side.

The improved rotator driver retries movement sensibly, parses rotctld replies, avoids redundant **Track** actions and handles stop/disconnect transitions. A new direction-wheel popup is available from the rotator/status right-click entry for manual jog; its directional buttons and PARK action are intended for situations when automatic antenna tracking is not active.

**Safety:** Manual jog is locked while SkyRoof is actively tracking a satellite pass. Stopping tracking may be necessary before enabling manual controls. Always observe the antenna and stop motion if limits or offsets are wrong.

### Ordered multi-waypoint PARK

Right-click **PARK** to edit the saved route/preset. The editor supports ordered **Add / Update / Delete / Move** waypoints (azimuth + elevation), with a single target still supported for compatibility. SkyRoof executes the route **sequentially**, not as simultaneous AZ/EL jumps across all waypoints.

The PARK state machine proceeds only after **two separate, fresh rotctld position replies** within approximately **1.5°** of the current target. It aborts if feedback becomes stale (over **12 s**), is invalid, disconnects, or the current leg exceeds **5 minutes**. Azimuth is compared as a **mechanical angle** — no modulo 360 normalization for a 450-degree rotator. The route is cancelled by tracking/manual jog/STOP as appropriate. Do not interpret a rotctld command ACK as proof the antenna moved.

## Interface, saved configuration and themes (PRs #12–13 and #30–35)

Under **Tools → Theme**, current choices include **System**, **Light**, **Dark**, **GitHub Light** and **GitHub Dark**. The fork's light palette includes **`#5BCEFA` blue**, **`#F5A9B8` pink**, and **`#FFFFFF` white**. The two GitHub themes use their respective light/dark design tokens.

Theme mode is persisted in `Settings.json` and initialized **before** creating the dock host; depending on the UI toolkit, changing it may require restart or re-creation of views. The spectrum's instrument trace may use its own high-contrast presentation independent of the general application theme.

The UI stores docking layout, group/list widths and settings. Settings writes are made with temporary-file replacement and a `Settings.json.bak` backup. A damaged file can be preserved as a redacted `.damaged-*.json` copy, then recovered from backup or defaults. Sensitive stored credentials are protected with Windows DPAPI where supported and legacy plaintext fields are migrated.

The top toolbar supports IC-9700 **RF gain through SkyCAT**, and separately selected **RS-BA1 playback-session AF gain**. See [CAT and FT4](radio-and-ft4.md) for these distinctly scoped controls.

## Maintenance notes

- Source and UI feature tests are maintained in the Windows [Compile Check](https://github.com/Iu-yang1/SkyRoof/actions/workflows/compile-check.yml) workflow.
- The WinDivert installer packaging path checks a pinned upstream archive hash and supports compatible in-use driver handling.
- The application remains compatible with upstream SDR, telemetry, SSTV, SSDV, QSO logging/ADIF, recording and pass planning workflows unless explicitly noted. See [the original user guide](../users_guide/overview.md).

See [Rotator Control](../users_guide/rotator_control.md), [Setting Up Rotator Control](../users_guide/setting_up_rotator_control.md) and [Data Folder](../users_guide/data_folder.md).
