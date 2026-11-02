# SkyRoof Plus

**Language:** English | [简体中文](README.zh-CN.md)

A Windows application for amateur radio and satellite operators. **SkyRoof Plus** is a community fork of [VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof), incorporating upstream v1.56 and additional features maintained in this repository. Features listed under **Fork extensions** belong to this fork; they are **not necessarily available upstream**.

[**Download latest release**](https://github.com/Iu-yang1/SkyRoof-Plus/releases/latest) · [English fork guide](docs/fork-guide/overview.md) · [中文扩展文档](docs/zh-cn/fork-guide/overview.md) · [Published documentation](https://iu-yang1.github.io/SkyRoof/) · [Changelog](FORK-CHANGELOG.md) · [Build status](https://github.com/Iu-yang1/SkyRoof-Plus/actions)

<p align="center">
  <img src="docs/images/skyroof_icon.png" alt="SkyRoof icon" width="140">
</p>

## Fork extensions

The following overview covers the features integrated through **SkyRoof Fork v1.64 (2 November 2026)**. See the linked guides for configuration, operational restrictions and validation status.

| Area | Added or enhanced in this fork | Details |
|---|---|---|
| **Orbit sources and ephemerides** | Configurable SatNOGS, CelesTrak OMM and manual TLE/OMM sources, fallbacks, and JPL DE440s/DE421/local BSP ephemerides for supported Solar System targets. | [Satellite data](docs/fork-guide/satellite-data-and-tuning.md) |
| **Frequency tuning and transmitter database** | Independent uplink/downlink base-frequency offsets, persistent corrections and reset actions; custom transmitter records (uplink-only, downlink-only or both); Doppler-aware tuning controls. | [Frequency and transmitters](docs/fork-guide/satellite-data-and-tuning.md) |
| **CAT and satellite remote operation** | SkyCAT/RS-BA1 integration for IC-9700, constrained WSJT-X CAT proxy, CI-V/PTT safeguards, CTCSS management, operator PTT controls and FT4 operating improvements. | [CAT & FT4](docs/fork-guide/radio-and-ft4.md) · [SkyCAT](docs/fork-guide/embedded-skycat.md) |
| **IC-9700 LAN spectrum** | Dockable spectrum and waterfall, scope controls, passband overlay, averaging and zoom; SkyCAT stream or passive RS-BA1/WinDivert capture. Independent Direct LAN remains **experimental**. | [Spectrum guide](docs/fork-guide/spectrum.md) |
| **IF → AF receiver for WSJT-X** | 48 kS/s **real 12-kHz IF** input, SSB/CW/FM DSP demodulation, mode-specific bandwidth/gain control, bounded buffering and virtual-cable AF output. Includes optional original basic VB-CABLE installer and IF/DDC/AF/V-MIC health indicators. | [IF → AF setup](docs/fork-guide/if-af-test.md) |
| **Doppler-assisted AFC** | FM and CW/pilot-based receive-frequency estimation, confidence gating, bounded **RX-only CAT** corrections, loss-of-signal HoldLast policy, spectrum and control diagnostics. It does **not** modify uplink/TX frequency. | [AFC guide](docs/fork-guide/afc.md) |
| **CW Console / Pileup** | Skimmer-style horizontal waterfall, multi-signal tracking and up to eight receive lanes, DeepCW-assisted decoding, continuous transcripts, optional HamNoise cleanup, and operator-triggered CI-V CW keying with F1–F8 macros. | [CW Console](docs/users_guide/cw_console.md) |
| **SSTV Console** | Dockable RX/TX, gallery and QSO log; 43 **transmit profiles**, VIS handling, AF/IF receive paths, editable images/templates with up to eight captions, UTC logging and ADIF export. Receive-mode support depends on the selected codec. | [SSTV guide](docs/fork-guide/sstv-console.md) |
| **Rotator and camera** | Feedback-verified multi-waypoint PARK, tracking/manual-control interlocks, GS-232B/rotctld improvements, native **RC-3040S LAN** control, and an ONVIF network-camera viewer using supported hardware video decoding. | [Rotator](docs/fork-guide/rotator-and-ui.md) · [RC-3040S](docs/fork-guide/rc3040s-lan.md) · [ONVIF](docs/fork-guide/onvif-camera.md) |
| **Interface and reliability** | GitHub Light/Dark and pink/blue/white themes, Chinese/English switching for Settings and selected control panels, layout/DPI fixes, configuration backup/recovery, credential protection, and Windows CI regression coverage. | [Fork overview](docs/fork-guide/overview.md) · [Changes](FORK-CHANGELOG.md) |

### IC-9700 and WSJT-X: example receive path

`IC-9700 USB 12-kHz real IF → SkyRoof IF/DDC/SSB-CW-FM DSP → AF → VB-CABLE → WSJT-X`

Set the radio's **USB AF/IF Output** to **IF** and select the correct Windows capture channel at 48 kHz. In SkyRoof Settings, enable **12 kHz IF → AF / WSJT-X**, select a dedicated virtual-cable render endpoint (for VB-CABLE, **CABLE Input**), then choose **CABLE Output** as the recording input in WSJT-X. **Never select the radio's transmit audio playback endpoint** for this route. The original basic VB-CABLE package is third-party Donationware; respect the [vendor's licensing terms](https://vb-audio.com/Services/licensing.htm).

[Detailed IF → AF configuration and caveats](docs/fork-guide/if-af-test.md) · [SkyCAT CAT/port ownership](docs/fork-guide/embedded-skycat.md)

### Availability and safety notes

- **Released versus experimental:** the fork's source and published releases include these modules, but hardware-specific behavior varies. In particular, independent IC-9700 Direct LAN spectrum access is experimental; AFC calibration/accuracy, radio-device latency, CW decode performance and rotator motion still require real-hardware validation.
- **TX and motion safety:** use authorized radio settings, check PTT ownership and CAT control, and verify the antenna's real position and mechanical limits before enabling tracking or motor commands. A passing CI suite is not physical acceptance testing.
- **Dependencies:** some capabilities require external software or hardware (e.g. RS-BA1, SkyCAT, WinDivert, VB-CABLE, an ONVIF camera and GPU-capable mpv). The application installer may be unsigned; inspect each [release](https://github.com/Iu-yang1/SkyRoof-Plus/releases).
- **Documentation scope:** the fork guide describes additions beyond upstream SkyRoof; the [upstream user guide](https://ve3nea.github.io/SkyRoof/) remains useful for standard satellite planning, SDR and telemetry functions. The hosted fork guide currently uses the existing `/SkyRoof/` GitHub Pages path.

## Building the solution

Open `SkyRoof.sln` with a compatible **Visual Studio 2026** Windows development environment and build using the repository's .NET / Windows Forms requirements (currently **.NET 10**). Check the [Compile Check workflow](https://github.com/Iu-yang1/SkyRoof-Plus/actions/workflows/compile-check.yml) for the tested configuration. See [fork installation guidance](docs/fork-guide/overview.md).

## Credits and community

- Original application: [VE3NEA/SkyRoof](https://github.com/VE3NEA/SkyRoof) · [upstream website](https://ve3nea.github.io/SkyRoof/)
- Fork-specific changes: [SkyRoof Plus history](FORK-CHANGELOG.md) · [contributors and credits](docs/credits.md)
- Community discussion: [SkyRoof Discussion Group](https://groups.google.com/g/skyroof)
