# IC-9700 spectrum and waterfall

**Language:** English | [简体中文](../../zh-cn/fork-guide/spectrum.md)

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
