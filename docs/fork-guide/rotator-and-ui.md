# Rotator, PARK routes, interface themes and reliability

**Language:** English | [简体中文](../zh-cn/fork-guide/rotator-and-ui.md)

## TCP connection recovery and diagnostics

The native TCP rotator worker distinguishes **transport connected** from
**protocol ready**. Each valid position frame is an application heartbeat;
one delayed/lost position reply no longer forces a TCP reconnect. Until
feedback returns, the UI reports **Synchronizing** and new motor commands
are inhibited. Three consecutive protocol timeouts trigger a clean reconnect.

Reconnection uses a short capped exponential delay (500 ms to at most the
configured **Reconnect Delay**, 5 seconds by default). The periodic Retry
signal can wake the reconnect worker, with a minimum retry interval to avoid
busy reconnect loops. An in-motion disconnect remains safety-locked with STOP
on reconnect; two fresh, separated feedback readings at the commanded
target may clear the settled-motion condition, but **manual jog never clears
its safety requirement without STOP**.

The status tooltip distinguishes **Reconnecting**, **Synchronizing**,
**Ready** and **Safety locked**. Detailed connection attempts, feedback
misses, recovery and socket exceptions are written to the normal log folder:
`%APPDATA%\Afreet\Products\SkyRoof\Logs`.

For TCP-to-serial bridges, check firmware, client/session limits and the
bridge's serial response terminator when feedback timeouts persist. TCP
KeepAlive is supplementary to protocol polling and does not prove motor
STOP. Never automatically restart manual motion after reconnection.

## Native C# rotator protocol implementation (experimental)

SkyRoof offers **Controller Driver: Native / LegacyRotctld** in **Settings → Rotator Control**. **Native is the default** for new configurations. Previously saved **Auto** entries are converted at load time: the normal localhost:4533 GS-232B configuration migrates to native serial; an existing nondefault remote rotctld TCP endpoint migrates to **LegacyRotctld**. The old **UseNativeDriver** field is hidden but preserved for backward compatibility. Native control does not need a running `rotctld.exe` process, and the **Enabled** control still determines whether any device is opened.

| Protocol | Native serial | Native TCP bridge | UDP | Feedback | STOP |
|---|---|---|---|---|---|
| GS-232 | yes | yes | — | C2 | S |
| RC-3040S LAN | — | yes (TCP/60020) | — | C2 | S |
| EasyComm I | yes | yes | — | unsupported | SA SE |
| EasyComm II/III | yes | yes | — | AZ/EL | SA SE |
| SPID ROT2PROG | yes | yes | — | binary | binary |
| SAEBRTrack | yes | yes | — | unsupported | unsupported |
| rotctld protocol | — | yes | — | p | S |
| OZ9AAR URC | — | yes | — | POLL | unsupported |
| PSTRotator | — | — | yes | passive | STOP |
| Custom Template | yes | yes | yes | configurable | configurable |

Settings show only fields relevant to your choice: **LegacyRotctld** shows its TCP Host/Port and legacy settings; **Native → Serial** shows COM and framing; **Native → TCP/UDP** shows **Rotator Network Host** and **Rotator Network Port** immediately below the transport selector and hides serial-only controls. **Custom Template** adds protocol command/response fields. Inactive settings remain saved when switching drivers/transports. Use **NativeProtocol**, **NativeTransport**, and the displayed device fields to select the actual controller. Initial GS-232B serial values are **COM9, 9600 baud, 8N1, no flow control**; change COM9 to the port detected in Windows Device Manager. Other controllers can set **NativeDataBits**, **NativeParity**, **NativeStopBits**, **NativeHandshake**, **NativeDtrEnable** and **NativeRtsEnable** in Settings. A separate rotctld instance must **not** simultaneously hold the same COM port.

The native **NativeRotatorIO** worker serializes STOP/manual positioning/telemetry off the WinForms UI thread, sends only distinct target frames, and latches motion upon invalid feedback or loss during movement. It does not permit new closed-loop positioning before the first fresh position report, discards superseded commands, caches SPID resolution per connection, and preserves unused serial/TCP receive bytes across response boundaries. Position reads remain possible when Track is off, at an independently configurable polling interval (1000 ms by default). Serial command writes do **not** constitute a motor STOP acknowledgment; ensure the controller has an independent physical emergency stop.

Protocols without feedback or STOP require an explicit **NativeAllowOpenLoopMotion** opt-in for positioning. Continuous-direction wheel commands are supported only for GS-232. The existing Track/PARK/angle limits and UI are preserved.


The automated acceptance suite includes synthetic TCP-serial controller responses and a **20-second idle endurance check** of command rate, process CPU time, thread count and handle count. This is not a replacement for a real COM9 motor safety test or a continuous 2/8-hour powered hardware soak. Always keep a physical emergency stop available.

**Status:** integration is experimental until physical COM9 direction, limits, STOP-on-release, unplug/reconnect, 0°/360°/450° crossings and an extended CPU/handle soak are verified. Do not test unattended.


## Hamlib rotctld rotator (PRs #22–23 and #25–28, #34)

Configure **Settings → Rotator Control** with the `rotctld` host and TCP port, normally **127.0.0.1:4533**; adjust azimuth/elevation limits, offsets and step size to the *real mechanical antenna and cable geometry*. A typical external rotator can be served by Hamlib with GS-232B-compatible hardware. The correct COM port, baud rate and protocol are configured in `rotctld`, not on SkyRoof's TCP side.

The improved rotator driver retries movement sensibly, parses rotctld replies, avoids redundant **Track** actions and handles stop/disconnect transitions. A direction-wheel popup is available from the rotator/status right-click entry. Its four sectors now use true continuous Hamlib movement: mouse-down sends `M direction -1` (UP=2, DOWN=4, LEFT=8, RIGHT=16) and mouse-up/capture-loss sends `S`. This matches GS-232B-style start-motion/stop semantics instead of repeatedly synthesizing absolute `P` targets.

The popup **Track step** is bound to the real `RotatorSettings.StepSize`. It is saved immediately and rebuilds the current optimized pass path, so changing it affects both the tracking command threshold and the path optimizer without waiting for a new pass.

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


## Feedback-angle jump safety

Two fresh rotctld angle replies within two seconds that differ by more than the configurable **Position Jump Limit** (default 30° on either axis) immediately latch a safety fault and request Hamlib **S STOP** from the I/O worker. SkyRoof reports whether the STOP was actually acknowledged, blocks further automatic/manual movement and asks the user to inspect the physical antenna position and feedback wiring. 359° → 1° is treated as a circular crossing, while extended mechanical readbacks such as 450° retain their actual turn. Only reapplying Rotator Control settings after inspection unlocks movement; reconnecting does not. This detects feedback discontinuities, not slow stalls, and a rejected STOP cannot guarantee the motor is stopped.

## Track CPU load and GS-232B positioning granularity

SkyRoof performs its normal position query (`p`) every 300 ms by default, regardless of whether Track is enabled. When Track is active, it computes new absolute targets (Hamlib `P az el`) approximately once per second if the antenna is outside the configured tracking error threshold. The GS-232B Hamlib backend rounds these target angles to **integer degrees** before generating the physical `Waaa eee` serial command.

To avoid multiple `P` commands that ultimately mean the exact same physical target, SkyRoof now coalesces duplicate commands at the selected **Position Command Resolution (deg)** in Rotator Control settings. The safe generic default remains **0.1°** (preserves high-resolution rotators). For a **GS-232B controller**, choose **1.0°** and reapply settings. Distinct integer-degree targets, manual commands, PARK sequences, safety STOP and reconnection continue to work. A successful `S` clears the command cache so an identical target can be requested again after STOP.

This eliminates redundant motion commands but is **not a proven explanation for 20% CPU usage inside rotctld.exe**. Hamlib serial-port behavior, other clients and driver retries require direct process/thread sampling if the load continues. To isolate them, compare idle, manual movement, Track, and Track with SkyRoof disconnected from port 4533; inspect rotctld command logs if necessary. Do not disable the safety STOP or the 300 ms position feedback just to lower CPU.

## RC-3040S direct LAN replaces the former NRL2 UDP driver

The former experimental DTrac NRL2 UDP driver and its RotorID/SID settings are **removed from SkyRoof**. The protocol source and historical settings schema were archived in [DTracNRL2.NET](https://github.com/Iu-yang1/DTracNRL2.NET/tree/main/docs/archive/skyroof-nrl2). The active protocol is **Native / Rc3040sLan** using TCP/60020 for GS-232 motion and WebSocket/60030 for on-demand AT management. Existing NRL2 protocol ID 10 is reserved and cannot start an alternative LAN connection. See [RC-3040S LAN guide](rc3040s-lan.md).
