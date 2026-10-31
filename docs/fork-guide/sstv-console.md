# SkyRoof SSTV Console — operator preview

> **Development build only.** This describes the open Draft [PR #130](https://github.com/Iu-yang1/SkyRoof/pull/130), not a released or fully hardware-validated SSTV application.

## SSTV workspace

Open **View → SSTV RX / TX** and select a page: **Receive**, **Transmit**, **Gallery**, or **QSO Log**. Windows can be docked or floated, with concise operator-facing controls.

### Receive and tune

Choose a live capture endpoint: AF, RS-BA1 playback loopback, real 12-kHz IF, or IF loopback. Select **Start Receive**. The SkySignals Robot/PD decoder and new RGB/extended/narrowband VIS raster receiver share the audio stream. The latter is experimental and has separate partial-image and end-to-end tests.

**Automatic frequency correction** (on by default) recognizes validated standard VIS leaders and applies a software AF-pitch correction before Robot/PD demodulation; the extra raster receiver additionally tracks residual sync tone during successive image rows. Manual `校频 Hz` shifts receive AF by ±500 Hz. Neither SSTV subsystem changes satellite uplink or persistent transponder frequency. SkyRoof's independent CAT/IF sign calibrator is unchanged in ownership.

When a VIS preamble is lost to fading, choose an explicit RGB/extended mode and click **Manual Start** near the picture start. Select **Import WAV** to decode a recorded 48-kHz 16-bit mono picture. The imported file is automatically identified by valid 8-bit VIS, extended 16-bit VIS, or N-VIS-24, unless an operator selected a manual mode. Capture can begin after a short silence or a leading audio sequence. Save and browse images with their UTC mode/source metadata.

**Caveats:** Automatic mode identification does not guarantee correct decoding during fading, frequency drift, multipath, clock slant, or partial scan-line loss. Live RX and offline RX have different acquisition paths. RS-BA1 loopback captures a whole playback endpoint, including any other applications on it. Prefer a dedicated audio endpoint.

### Create an image

Open/paste, crop-to-mode ratio, rotate, mirror, undo/redo. Select a classic callsign, QSL card, satellite banner, or plain image; save custom reusable templates with overlay text. Macros: `{CALL}`, `{MYGRID}`, `{HISCALL}`, `{SAT}`, `{MODE}`, `{UTC}`, `{DATE}`, `{TXHZ}`, `{RXHZ}`. Supported sender modes are the 43 named MMSSTV Robot, PD, Scottie, Martin, SC2, P, MP, MR, ML and MP-N/MC-N variants; all produce WAV via a dedicated offline encoder. VIS may be standard 8-bit, extended 16-bit, or narrow N-VIS 24-bit as appropriate.

**Save SSTV WAV** exports audio without using CAT/PTT. The **Transmit** controls additionally select a dedicated audio playback device, require explicit band/power approval and an extra confirmation, and provide **Stop**. These remain an **experimental RF pathway**.

### QSO Log

Save a QSO only after manually entering a valid partner callsign. Store UTC, satellite, selected SSTV mode, TX/RX frequencies and an optional received picture. Export ADIF. Receiving a picture alone does not automatically create a QSO.

### CAT/PTT and satellite safety

For an experimental transmission the code requires a supported active VHF/UHF satellite uplink, USB/FM mode, selected audio output, live CAT actual TX-frequency readback, available application PTT lease, and hardware PTT readback. A watchdog monitors satellite visibility, frequency and Doppler, mode changes, device failure and user STOP. Exceptions request release of the application's own PTT, not any unrelated application's lease. **Application PTT and CAT readback are not equivalent to independent RF output measurement.**

**Before using an IC-9700 for RF**, a supervised dummy-load test must verify the actual RS-BA1/USB/LAN audio injection route, low-power spectrum, RF level, safe STOP, PTT OFF after CAT interruption and watchdog expiry, and long transmissions with continuous uplink Doppler. Those physical tests have NOT been performed by this development branch.

## Compatibility and release gates

The 43 transmit code paths and streaming/mode-based receive paths exist, but **MMSSTV full feature parity is not yet independently demonstrated**. The MMSSTV reference also includes AVT sync/phase, RX slant and sample-clock calibration, many filter/denoise/presentation controls, FSK/CW identification, legacy repeater/contest/QSL integration, and OLE/COM functionality. Mode tables and waveform unit tests are not a substitute for a reference golden audio corpus and independent demodulator comparisons for *every* mode under AWGN, Doppler, QSB, fading and clock drift. Real IC-9700 hardware tests remain mandatory for a stable RF-capable Release.

The [P0–P4 audit plan](../architecture/sstv-console-integration-plan.md) tracks remaining acceptance criteria. **Do not merge to master or label a stable Release before they pass.**

## Reference and licensing

- [MM-SSTV source](https://github.com/n5ac/mmsstv)
- [SkySignals decoder](https://github.com/VE3NEA/VE3NEA.SkySignals)
- [PR and current Windows CI](https://github.com/Iu-yang1/SkyRoof/pull/130)

SkyRoof remains AGPLv3. Third-party implementation and mode definitions require appropriate copyright and license attribution.
