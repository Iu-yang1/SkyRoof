# SkyRoof fork: overview and installation

**Language:** English | [简体中文](../zh-cn/fork-guide/overview.md)

This guide documents **[Iu-yang1/SkyRoof](https://github.com/Iu-yang1/SkyRoof)** at the fork's `master` branch, based on the upstream **VE3NEA/SkyRoof v1.55** codebase. It describes the implemented fork additions; it does not imply they are available in [upstream VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof). The original [English user guide](../users_guide/overview.md) remains the reference for basic panels, SDR operation, telemetry, SSTV/SSDV, FT4, ADIF, satellite groups and general settings.

## Installation and supported environment

- **Platform:** SkyRoof is a Windows desktop application built with .NET 10 / Windows Forms. Build the `SkyRoof.sln` using the toolchain and Windows build configuration documented by the [repository](https://github.com/Iu-yang1/SkyRoof). Check the current [Compile Check](https://github.com/Iu-yang1/SkyRoof/actions/workflows/compile-check.yml) before using a particular commit.
- **Distribution:** See the [fork's Releases](https://github.com/Iu-yang1/SkyRoof/releases), [Actions](https://github.com/Iu-yang1/SkyRoof/actions) and the original [Download](../download.md) page. A successful compile-check or unsigned test-installer artifact is **not necessarily a signed public release**. The upstream v1.55 installer does **not** include all fork additions.
- **Optional dependencies:** [SkyCAT fork](https://github.com/Iu-yang1/SkyCAT) for CAT/CI-V, [RS-BA1](https://www.icomjapan.com/) for IC-9700 remote operation, Hamlib `rotctld` for a rotator, and **WinDivert** for passive LAN spectrum capture. Each feature has separate setup requirements.
- **Before first launch:** Set the correct station location and radio interfaces. For TX or moving antennas, start with PTT disengaged and the rotator in a safe position.

## What the fork adds

| Area | Features available in this fork |
|---|---|
| Satellite data | Configurable SatNOGS, CelesTrak OMM CSV, manual TLE/OMM sources, AutoTLE fallback; JPL DE440s/DE421 or compatible local BSP kernel for Moon/Sun/Venus |
| Frequency and Doppler | Persistent per-transmitter base-frequency corrections, separate uplink/downlink controls, base/reset actions, tuning bar and corrected versus non-Doppler displays |
| Transmitters | Locally defined, editable and removable transmitter records; uplink-only, downlink-only and two-way entries |
| CAT and remote | SkyCAT integration, hardened CAT/PTT transitions, global physical/HID PTT key, CTCSS handling, FT4 safety updates and NAudio/WASAPI migration |
| IC-9700 spectrum | New dockable scope with native SkyCAT source, RS-BA1 passive LAN capture and experimental independent Direct LAN; spectrum/waterfall and scope settings |
| Rotator | Improved Hamlib rotctld state handling, manual direction-wheel card, tracking lockout and feedback-verified multi-waypoint PARK routes |
| Interface | Light theme with blue/pink/white design (`#5BCEFA`, `#F5A9B8`, `#FFFFFF`), GitHub Light/Dark themes, RF gain via SkyCAT and selected RS-BA1 Windows playback AF gain |
| Reliability | Recoverable settings backups, protected saved credentials, source validation and regression-tested packaging and Windows CI |

## Recommended IC-9700 coexistence architecture

| Component | Connection | Ownership |
|---|---|---|
| RS-BA1 Remote Utility | IC-9700 LAN and its virtual CI-V COM | Remote LAN session, radio audio |
| SkyCAT `skycatd` | One RS-BA1 CI-V virtual COM | Serial command queue and client arbitration |
| SkyRoof CAT | `127.0.0.1:4532` | Satellite tuning, Doppler and radio control |
| WSJT-X (optional) | SkyCAT `127.0.0.1:4534` | Restricted rigctl proxy |
| SkyRoof native scope (optional) | SkyCAT `127.0.0.1:4535` or passive RS-BA1 LAN | Spectrum samples |
| Remote Control Switch (optional) | SkyCAT `127.0.0.1:4537` | Restricted auxiliary radio settings; no VFO/tuning/PTT |
| Hamlib rotctld | Default `127.0.0.1:4533` | Antenna pointing |

**Important:** Distinct TCP ports do **not** create separate radio CI-V links. Only one program should own the underlying virtual COM. See [CAT and FT4](radio-and-ft4.md), [spectrum](spectrum.md) and the [SkyCAT guide](https://iu-yang1.github.io/SkyCAT/skycatd.html).

## Guide navigation

[Satellite data and tuning](satellite-data-and-tuning.md) · [CAT, FT4 and remote](radio-and-ft4.md) · [IC-9700 spectrum](spectrum.md) · [Rotator and interface](rotator-and-ui.md) · [Troubleshooting and PR history](troubleshooting-and-history.md).

**Feature-status convention:** A capability described as **experimental** is present in source code but should not be treated as production-ready. Pages describe the current repository implementation, not a promise that every radio/firmware supports every command.


## Uniform window zoom (no reflow or scrollbars)

The experimental wrapping/scrolling dock layout of PR #84 was reverted. The main radio toolbar and Frequency Control now use **uniform proportional zoom**, resizing controls, their click targets, horizontal and vertical spacing, and fonts by the *same scale factor*, measured from an immutable design-size baseline to avoid cumulative rounding errors.

- **Main toolbar**: the satellite selector, photo, RX/TX frequency controls, gain, rotator, PTT and clock keep their original order and remain visible; the complete strip shrinks as the main window narrows.
- **Frequency Control**: the two Base groups and Tuning group keep their original two-column arrangement and relative positions; the entire surface scales to fit the available dock area without overflow scrolling.
- Font size also reduces when the pane becomes narrow. This is intentional and may be small at extreme window sizes. Increase the docked pane size to restore legibility.
- A follow-up implementation extends the fixed baseline viewport to **CW Console, Icom LAN Spectrum, ONVIF Camera and QSO Entry**, and DPI-proportional sizing to the Rotator Control popover. The scope/waterfall custom controls and mpv's native video host keep using their scaled WinForms client rectangles; no FFT, decoder, CAT or rotator commands are changed by the zoom code.
- The Icom scope toolbar is laid out at a fixed design width **once**, then frozen. CW toolbars no longer scroll and the QSO cards retain fixed grid locations instead of flowing at runtime. Avoid docking extremely small panes: proportional text can become unreadably small.

### Windows and hardware acceptance checklist (not CI-certified)
- At **100%, 125% and 150% display scaling**, reopen and resize all five views repeatedly, floating and docked; verify complete status text, TX macros, frequency readout, QSO cards, video chrome and cursor targets. Confirm no new scrollbars, wrapping, clipped content or cumulative geometry drift.
- With CW receive active, check waterfall click-to-select, AF pan and spectrum frequency picks after resize. With Icom scope active, verify mouse-wheel zoom, tuning clicks, waterfall history and 475-bin frame alignment; compare displayed RF readback with RS-BA1.
- With ONVIF video active, confirm the native mpv child window follows the scaled black video host (not only its background) and hardware decode remains D3D11VA. Check mouse interactions and disconnect/reconnect.
- **Rotator safety:** use a simulator first, then a physical GS-232B with a separate hardware E-stop. Inject an impossible feedback jump; verify latched STOP, independent STOP acknowledgement and no unintended Track/PARK restart. Measure rotctld CPU before/after 1-degree coalescing with Track enabled.
- **ONVIF discovery:** test a physical camera on each active network adapter, probe responses, Device Service HTTP port versus RTSP 554, Media1/2 profile negotiation, and player reconnect behavior.
- Headless xUnit checks cover structural geometry, zoom factors, CW/QSO fixed layouts and direction wheel hit-testing. They **cannot** certify interactive desktop DPI, live camera GPU rendering, radio signals or physical safety.
