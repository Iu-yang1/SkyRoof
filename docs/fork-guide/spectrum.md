# IC-9700 spectrum and waterfall

**Language:** English | [简体中文](../zh-cn/fork-guide/spectrum.md)

The fork introduces a dockable **Icom LAN Spectrum** panel separate from SkyRoof's original SDR waterfall. It consumes IC-9700 CI-V **27 00** scope data (normally a **475-bin** spectrum sweep) and provides a scope trace, optional waterfall, frequency markers, peak hold, averaging and display controls. It is not a replacement for the original SDR's baseband samples or a demodulator.

Open **Icom LAN Spectrum** from the application's panel/window menu. Configure **Tools → Settings → Icom LAN Spectrum** first.

## Choose the source separately from the control path

| Source in settings | Requirements | Important behavior |
|---|---|---|
| **SkyCat** | SkyCAT's native scope TCP stream at `127.0.0.1:4535`; active SkyCAT CI-V connection | Scope data emitted from the existing SkyCAT serial transport; does not start another IC-9700 LAN session |
| **RsBa1** | RS-BA1 running, LAN CI-V traffic, installed WinDivert, appropriate privileges | **Passive UDP sniffing** of RS-BA1's existing session; does not log in to the radio; may stop when RS-BA1 or its spectrum output closes |
| **DirectLan** | Radio IP, LAN credentials, available remote session | **Experimental authenticated client**; manual Start only, can conflict with RS-BA1; avoid running both as competing remote clients |

The **Scope control path** is a **different** setting: `Auto`, `SkyCat`, `DirectLan` or `ReadOnly`. Auto follows the selected source, but **passive RS-BA1 is read-only in Auto**. To control scope settings while observing passive RS-BA1 frames, explicitly choose **SkyCat** and maintain SkyCAT CAT access. `ReadOnly` never writes scope controls.

**Recommended with RS-BA1 + SkyRoof:** choose **RsBa1** as waveform source and **SkyCat** as control path when the currently connected SkyCAT supports IC-9700 scope commands. If no control path is available, continue in read-only mode. Do **not** open the experimental independent Direct LAN session merely to improve FPS.

## Instrument-style scope controls

The updated panel includes a trace and optional waterfall with adjustable splitter height, zoom, scroll, hold/peak-hold reset, and:

- MAIN / SUB or Auto scope band selection;
- CENTER / FIXED / SCROLL display mode, supported spans, fixed-edge selection, reference level;
- sweep speed, VBW, TX display, center reference and marker placement where accepted by the radio;
- local spectrum averaging and smoothing, waterfall brightness/contrast/palette/history;
- radio-frequency markers and mouse-assisted tuning where the active CAT mode permits writes.

Frequency/control readback is **incremental**: the application asks SkyCAT for one verified field per CAT cycle and preserves partial readback if a radio/firmware does not support a register. This avoids monopolizing the CAT/PTT path while reading many scope registers. A failed individual readback does not mean all settings are confirmed; the panel can indicate synchronization pending or read-only.

The **local displayed frame rate** is not necessarily the IC-9700's complete sweep rate. With RS-BA1 LAN, a complete 475-bin `27 00` frame may arrive as a single CI-V message, while the virtual COM path may deliver multiple serial chunks. The assembler supports both paths, but should not promise a fixed FPS.

## WinDivert and permissions

Passive RS-BA1 capture needs a compatible **WinDivert DLL/driver** and administrative rights to capture supported UDP traffic. The Windows installer/workflow prepares **WinDivert 2.2.2 x64**. During reinstall, an already-loaded `WinDivert64.sys` may remain in use; do not remove the live driver or terminate RS-BA1 blindly. Reuse a compatible existing driver when supported, or stop the dependent applications before changing the driver.

There are three typical independent failure causes:

1. RS-BA1 closes its spectrum window or session, so passive waveform traffic no longer exists;
2. SkyCAT is connected for CAT but **4535** has no native scope frames (serial scope readback/source limitations);
3. The scope is receiving frames, but **control** is read-only/unavailable. A visible trace does not prove a setting was written to the radio.

Treat Direct LAN as **lab/experimental** only. Credentials are protected in saved settings, but never publish network login details or raw authenticated packet dumps in bug reports.

More background: [SkyCAT documentation](https://iu-yang1.github.io/SkyCAT/skycatd.html) · [original SDR waterfall](../users_guide/waterfall_display.md).


### Confirming spectrum controls (50 kHz span, mode, REF, SPEED, VBW)

The Icom LAN Spectrum waveform comes from received CI-V `27 00` frames.
**A queued CI-V write is not confirmation that the radio has applied a control.**
Previously every arriving scope frame could reset the Span selection back to
the old reported value (e.g., 50 kHz immediately returned to 25 kHz).

The panel now maintains per-receiver **pending control state** for operator
requests routed to SkyCAT. The MODE/SPAN selector shows the requested value
while awaiting the real MAIN/SUB `27 00` frame (or a matching scope-register
readback), and the adjacent geometry text continues to show radio-reported
frequency coverage. A pending SPAN also receives a `pending` label.
REF, SPEED, VBW and EDGE changes are protected from stale asynchronous
readbacks. These are separate from display-only **HOLD, PEAK, waterfall,
averaging, smoothing and zoom** controls.

A successful enqueue means **queued**, not applied. A setting is confirmed
only after matching hardware evidence. If the radio does not confirm it
within 20 seconds, the request expires, the actual reported mode/span becomes
visible again, and the panel requests a fresh SkyCAT readback. The Spectrum
Diagnostics window includes the number of changes still awaiting confirmation.
Pending changes are cleared on capture restart, control-backend changes, and
source switches.

**Transport limitation:** a passive RS-BA1 waveform capture is read-only by
itself. To modify IC-9700 scope registers with passive waveform data, select
**SkyCAT** explicitly for the *Scope control path* in Settings, ensure the
SkyCAT radio control backend is online, and inspect its command logs if a
write times out. Direct LAN scope writes remain experimental and are not
silently claimed to be supported.


### Spectrum dropdown stability with high-rate scope frames

The Icom LAN spectrum display and the CI-V radio-control selections no longer
share a one-to-one UI update cadence. Incoming `27 00` waveform frames are
coalesced into **at most one pending WinForms callback**, using the newest
frame when the UI is temporarily occupied. Only actual mode/frequency/span
geometry changes refresh the radio-control selector values, rather than
every amplitude-only frame. The normal 500-ms status/readback poll still
handles backend reconnection and incomplete metadata.

Opening, hovering, or keyboard-focusing the **MODE, SPAN/EDGE, SPEED or VBW**
dropdown prevents incoming scope data from rewriting its active selection.
A write command is now emitted on **SelectionChangeCommitted**, not on a
programmatic selection change during UI synchronization. REF numeric edits
are similarly protected while focused. The prior queued-versus-confirmed
radio write handling remains in effect: a selection that eventually reverts
after the 20-second confirmation timeout is a radio-control/CI-V issue and
should be diagnosed through SkyCAT, not hidden as a successful change.

Coalesced UI scope frames and pending/unconfirmed CI-V commands are shown in
Spectrum Diagnostics. The latest frame is preferred over replaying old
waveform frames, preserving the existing Direct LAN, SkyCAT and passive
RS-BA1 capture behavior while reducing contention with CW Console painting.


### Red RX passband overlay: bandwidth accuracy, radio modes and zoom

The red shaded area is an **estimated IC-9700 receive IF passband**, not
a visualization of the independent SkyRoof SDR `Slicer` filter.
Previously the scope used `Slicer.GetBandwidth` / `GetModeOffset`
directly: 2.8 kHz SSB, 5 kHz SSB-D, 500 Hz CW and 16/48 kHz FM.
In particular, 48 kHz FM-D was not representative of IC-9700 FM-D
IF selectivity.

The estimator now uses the nominal IC-9700 widths and offsets for
the modes currently exposed by SkyRoof's RadioLink:

| Mode | Nominal estimated width | Nominal RF region relative to dial |
| --- | ---: | --- |
| USB | 2.4 kHz | +300 to +2700 Hz |
| LSB | 2.4 kHz | -2700 to -300 Hz |
| USB-D | 1.2 kHz | +900 to +2100 Hz |
| LSB-D | 1.2 kHz | -2100 to -900 Hz |
| CW | 500 Hz | centered on dial |
| FM / FM-D | 15 kHz | centered on dial |

These are **illustrative FIL defaults**, not measurements of your radio.
FIL1/FIL2/FIL3, custom IF BW, IF shift / Twin PBT, CW pitch, and mode
changes made independently in RS-BA1 are not currently read back as a
complete IF passband from scope CI-V `27 00`. Do not treat this shading
as a calibrated measurement of receive selectivity. AM, RTTY, DV and DD
are not represented by the current `Slicer.Mode` overlay interface;
SkyRoof will not invent a passband for an unknown mode.

In Spectrum **Settings**, four *Estimated RX ... filter width (Hz)*
fields let you match the red overlay to the width shown on the radio
for SSB, SSB-D, CW or FM/FM-D. These settings affect the overlay only:
**they do not send CI-V commands to change the IC-9700 filters**.

The span/zoom/center/fixed frequency-to-pixel mapping uses precisely the
same scope geometry and optional CAT/transverter display-frequency offset
as the waterfall, trace and tuning marker. An estimated passband outside
the visible view is clipped completely, rather than leaving a spurious
one-pixel red stripe. USB/LSB polarity, 25/50/100 kHz span and zoom ratios
are covered by regression tests.
