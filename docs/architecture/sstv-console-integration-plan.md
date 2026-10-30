# SSTV Console: MMSSTV feature-parity integration plan

Status: P0 foundation / design. This file is an implementation plan, **not** a claim that feature parity or on-air TX is available.

## Reference and baseline

- SkyRoof `master` (2026-10-31 audit): .NET 10/WinForms, existing `TelemetryDecocder` delegates satellite SSTV RX to `VE3NEA.SkySSTV.SstvDecoder`; `TelemetryPanel` archives received images and offers SSTV save filters.
- SkyRoof has existing `AfcController`, `AfcIfInput`, `CatControl`, `CwTransmitInterlock`, `Soundcard`, `RsBa1AudioSessionController`, `RadioLink`, satellite Doppler.
- Upstream https://github.com/n5ac/mmsstv is C++ Builder/VCL, with LGPL-3.0-or-later headers and `COPYING.LESSER.txt`; the SkyRoof repository contains AGPLv3 `LICENSE`.
- Legacy VCL controls, Windows audio/serial APIs and image UI cannot be dropped into managed WinForms directly. Reuse protocol definitions and DSP after independent extraction/porting; explicitly record borrowed code, authorship and distribution obligations. **P0 tracker below is original SkyRoof code; it does not copy MMSSTV implementation.**

## Functional parity matrix (each row requires a source-to-target audit)

| Group | MMSSTV reference / required capability | SkyRoof baseline | Target acceptance |
|---|---|---|---|
| RX modes | Robot BW 8/12, Robot 24/36/72, AVT90, Scottie 1/2/DX, Martin 1/2, SC2 60/120/180, PD50/90/120/160/180/240/290, P3/P5/P7 | Robot 36/72 and PD family in existing satellite decoder | Golden-vector decode for every legacy mode, automatic VIS and manual fallback |
| Extended RX/TX | MP73/115/140/175, MR73/90/115/140/175, ML180/240/280/320; MP-N and MC-N narrowband, 16-bit VIS and N-VIS | Not audited as supported | Match source timings, scan lines, VIS/N-VIS, deterministic test images |
| RX controls | VIS/sync auto-start, manual start, squelch, auto-stop/restart/resync, phase, slant/clock correction, RX history/recording, FFT spectrum, tune | Partial auto mode and file save | All mode controls operable on live + saved WAV, recover shifted/drifting and truncated image |
| RX signal processing | IF 12 kHz, USB/LSB demodulation, AF capture, RS-BA1 loopback, SDR source; BPF, notch, LMS/noise mitigation | Source-dependent SDR/AF/IF infrastructure | Explicit source and gain/clip status; IF must first be demodulated, never treated as ready AF |
| TX | Image-to-SSTV encoder, VIS, mode selection, AF device selection, calibration/clock, output level, tune/test, CW ID and FSK ID | No comparable native SSTV TX | PCM/WAV golden-vector compatibility and real-loopback test, safe idle/PTT transitions |
| Image workflow | Stock/gallery, history, clipboard and drag/drop, image fit/crop, compositing/templates, transparent color, callsign macros, text/graphics overlays | Partial received-image viewer | Editable TX preview, saved/reusable templates, undo/redo, deterministic render |
| Log and persistence | QSO log, RX/TX image association, temporary RX audio buffer, per-pass snapshots, export, auto-save and retention | Telemetry archive | UTC metadata, satellite/mode/frequencies, bounded working store, cleanup, recover-on-restart, opt-in export |
| Integration and extras | CAT/PTT, legacy COM/RTS/DTR/EXTPTT, MMlink, contest/QSL/logbook features, OLE/custom objects and repeater | Existing CAT but not MMSSTV parity | Feature-by-feature compatibility/equivalent design; explicit unsupported ledger until complete |

*The original English manual and `mode.txt`/update notes are the mode authorities, not assumptions derived from SkyRoof's existing decoder. Each matrix cell must be backed by tests before marking complete.*

## Architecture

```text
                    SkyRoof SSTV Console (DockContent)
    RX pane / FFT / sync / slant / image / history / TX editor / log
                            |
    RX source broker --------------------------------- TX sink broker
    SDR complex -> SSB/FM demod                        PCM -> AF output
    USB/RS-BA1 AF -> normalized float @ 48k             -> CAT/PTT lease
    12k IF -> mix/channel filter -> SSB demod          -> TX watchdog
             |
    VIS & N-VIS / sync detectors -> mode decoder -> image + metadata
             |
    SSTV AF-offset estimator (software-only, P0 foundation)
             -> analytic AF NCO/pitch correction
             -> optional RX CAT trim (only after independent verified sign)
    Doppler/orbital model remains the only authority over baseline RX/TX
```

Keep legacy satellite SSTV decoder and TelemetryPanel functioning unchanged. New console must integrate via a separately owned tap with bounded queues and epoch identifiers. No GUI work on DSP threads. Do not have two owners write CAT simultaneously. RX AFC reports a **volatile correction** only; never mutate saved transponder/base frequencies or TX offsets.

The SSTV frequency observable is the residual of known 1200 Hz sync, VIS 1100/1300 Hz and 1500–2300 Hz image spectral components after demodulation. A blind spectral peak in ordinary SSB is not an absolute SSTV reference. Gate measurements on validated VIS/sync cadence and line timing. First estimate constant frequency error, then its drift via robust alpha-beta/Kalman filtering; on mode, satellite, transmitter, sample-path or sample-rate changes, invalidate previous epochs. Delay CAT changes until RF/IF sign has been verified by closed-loop CAT write/readback and the output path is unambiguously associated. Software compensation must operate on an **analytic AF representation** (Hilbert/complex NCO + suitable anti-alias filtering), not multiplication of mono AF by a real cosine.

For **linear transponder** operation, keep RX and TX frequency domains separate. Inverting-transponder Doppler/coupling and uplink sign require explicit checked transformations; received SSTV audio pitch **cannot by itself** identify the other station's desired uplink correction. While sending a long image, orbital uplink Doppler correction must remain operational under one CAT writer; the TX lease blocks conflicting user/AFC adjustments, **not** every tracking update. Auto CAT correction starts disabled and remains opt-in. Never repurpose CW interlock to freeze all TX Doppler for a 2–5-minute image.

## TX safety state machine

`Idle -> Armed -> Verify -> PTT On -> Pre-roll -> Sending -> Drain -> PTT Off -> Idle`.

- Arm requires intentional local operator action, valid selected rig/satellite/uplink mode, device selection, reasonable computed TX frequency, readback if supported, no conflicting CW/other PTT lease, TX audio route distinct from monitoring route where necessary.
- Enforce maximum duration, output clipping checks, CAT/PTT acknowledgements/timeouts and emergency STOP. Invalid context/LOS/device failure -> stop PCM, PTT off, release lease. Never silently key a radio during tests.
- Treat soundcard/Rig CAT as independent clocks; do not mark a transition successful before actual readback/ack. Keep a non-radio dry-run WAV TX backend for tests.
- A regulator/duty-cycle-aware operator reminder and manual band-plan confirmation are part of TX UX; no automatic unsupervised SSTV CQ.

## Milestones and gates

| Phase | Deliverable | Required exit gate |
|---|---|---|
| P0 | Source/license audit, feature matrix, module boundaries, SSTV-specific AF residual estimator, synthetic tests | Build/test workflow, no direct CAT writes, zero regressions |
| P1 | Complete RX modes and VIS/N-VIS, SDR+AF+12k IF source broker, slant/phase/sync, RAW WAV replay and image archive | Reference WAV corpus per mode, BER/PSNR and 0/±frequency drift comparisons, long-run leak/latency checks |
| P2 | All reference TX modes, AF output device, preview/editor/templates/gallery, dry-run WAV sink | Source-comparison waveform verification, max amplitude, sample-clock calibration and RX loopback |
| P3 | CAT/PTT leases and RX/TX Doppler policy, satellite AFC (AF-first, optional verified CAT), CW/FSK ID, logs/history | Mock CAT dropouts/conflicts, emergency STOP, RX across linear/inverting-transponder pass, real IC-9700 supervised TX after dry run |
| P4 | MMSSTV compatibility gap closure: legacy macros/logbook/repeater/contest and supported OLE equivalents; bilingual docs, end-to-end regression and release | Each feature matrix row either verified as equivalent or documented unresolved; all CI checks and operator acceptance |

## P0 estimator contract and test policy

`SkyRoof/SSTV/SstvAfFrequencyTracker.cs` consumes independently verified *AF-tone offsets*, not arbitrary spectral peaks or raw IF. Its output is an advisory `-frequencyOffset` for a future analytic-AF frequency-translator, **not** a CAT write or a working receiver. Updates reject low confidence, invalid samples, out-of-order timestamps, excessive innovations and stale epochs. Lock needs multiple consistent measurements. Tests target bounded drift, reset, glitch rejection and polarity independent from CAT. The P0 test file is a **proposed xUnit test**, pending build/CI verification.

## Test environments

- Deterministic generated SSTV WAV from an independently verified reference decoder/encoder; AWGN, QSB, fades, dropouts, drift ramps, strong co-channel interference and deliberately wrong VIS.
- 48 kHz AF, 12 kHz real IF (USB and LSB), SDR complex source, RS-BA1 loopback, latency and device interruption.
- Linear non-inverting and inverting satellites, uplink and downlink model, SkyCAT connected/disconnected, CAT readback sign fail, PTT race and watchdog.
- Regression: existing satellite SSTV/telemetry, CW, FT4, Doppler, telemetry archive, radio modes, localized themes, docks and audio routing.

## Audit references

- https://github.com/n5ac/mmsstv/blob/master/EMMSSTV.TXT
- https://github.com/n5ac/mmsstv/blob/master/mode.txt
- https://github.com/n5ac/mmsstv/blob/master/EUPDATE.TXT
- https://github.com/n5ac/mmsstv/blob/master/sstv.cpp
- https://github.com/Iu-yang1/SkyRoof/blob/master/SkyRoof/DSP/TelemetryDecocder.cs
- https://github.com/Iu-yang1/SkyRoof/blob/master/SkyRoof/AFC/AfcController.cs
- https://github.com/Iu-yang1/SkyRoof/blob/master/SkyRoof/CW/CwTransmitInterlock.cs
