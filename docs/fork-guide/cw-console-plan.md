# CW Console: full multi-lane Pileup and CI-V keyer implementation plan

**Status: receive core under quantitative validation.** The repository now contains multi-carrier detection/tracking, a dual-resolution frame-level ridge scanner on a monotonic PCM sample timeline, calibrated wideband DeepCW feature extraction, independent multi-lane ONNX/CTC inference, activity-aware interference masks, merge-group identity handling and a real-model benchmark. It still does **not** wire live SDR/WASAPI PCM into the CW Console UI, provide continuous transcript reconciliation, integrate HamNoise, or enable radio transmission.

Chinese: [CW Console 完整规划](../../zh-cn/fork-guide/cw-console-plan.md).

References: [e04/deepcw-engine](https://github.com/e04/deepcw-engine) provides the **actual ONNX CW text decoder**; [web-deep-cw-decoder](https://github.com/e04/web-deep-cw-decoder) supplies an architectural reference for Pileup detection, tracking and streaming display; [HamNoise](https://github.com/e04/HamNoise) supplies optional local C-based CW noise reduction.

## Scope and acceptance definition

A single dockable WinForms CW Console shall support native SDR AF, explicitly selected WASAPI input or RS-BA1 audio-loopback capture; raw/denoised monitoring; **independently decoded transcripts for each detected CW carrier**; waterfall selection; QSO helpers; and RS-BA1-style keyboard CW messages, F1–F8 macros, WPM, break-in, explicit abort, and satellite-specific preflight.

The independent deepcw-engine model metadata currently specifies 3,200 Hz, FFT=256, hop=48, 400–1200 Hz, 65 spectral bins, input [1,1,T,65], 42-class CTC output. Follow the model's own metadata when building preprocessing. The website uses different 9.6kHz frontend and additional detection/narrow models which must **not** be assumed to be included in deepcw-engine. For a redistributable independent implementation, build our own candidate detector and per-lane BPF/NCO frequency shifter, then run the publicly released DeepCW model on each lane. Multiple frequency-**separable** signals can be decoded; completely co-channel simultaneous CW cannot be universally separated from one mono channel.

SkyRoof, HamNoise and deepcw-engine are AGPL-family projects. Preserve notices and separately validate redistributable model assets/dependencies; independently implement WinForms UI rather than copying frontend code with no confirmed separate license.

## Pileup architecture: multi-target tracking plus component separation

Pileup is no longer designed as frame-local peak chasing followed by fixed rectangular filters. The implementation uses:

1. **Stateful TF ridges** — every CW component carries a Kalman state \`[frequency, frequency-rate]\` and covariance.
2. **Global association (GNN)** — all tracks and all spectral candidates are associated jointly per scan so changes in relative signal strength do not swap IDs.
3. **Birth / coast / death** — new detections create tentative tracks; short QSB or a missed peak coast on prediction; tracks expire only after a hold interval. A single peak produced by two close components is treated as a merged measurement rather than proof that one station disappeared.
4. **Ambiguity state** — two predicted ridges closer than the resolvable threshold remain two labels but are marked ambiguous/colliding. UI and logging must not claim reliable source separation in that interval.
5. **Activity-aware competing masks** — each confirmed track has a per-frame carrier-on probability derived from ridge energy with a two-state HMM. Soft Gaussian/Wiener-like masks use this probability, so a confirmed but currently key-up station does not steal another station's TF energy.
6. **All-track interference set, selected inference set** — every reliable track participates in the mask denominator, including lanes that are not selected for ONNX because of CPU limits. Only the top resource-selected lanes run DeepCW.
7. **Calibrated wideband STFT, independent inference** — the original PCM bandwidth is preserved. At 48 kHz the shared frontend uses a 3840-point / 720-hop periodic-Hann STFT, which has the same 80 ms / 15 ms / 12.5 Hz physical grid as DeepCW's 256 / 48 frontend. Magnitudes are calibrated back to the model domain before lane translation, so AF carriers above 1.6 kHz remain available.
8. **Merge groups with bounded labelled history** — close tracks are assigned an explicit MergeGroupId. A single finite-resolution peak may update the group's centroid for a short fixed lag while preserving each track's pre-merge frequency/rate identity anchor. The shared-peak interval explicitly inflates covariance so the first reliable split observation can recover the correct ridge.
9. **Dual-resolution frame scanner** — Fast STFT uses 80 ms / 15 ms and is activity/continuity evidence only; its observations are explicitly not eligible for Kalman updates. Precision STFT uses 240 ms / 120 ms, reports sample-index aligned frequency/SNR/resolution/measurement sigma/activity, and is the only frame stream sent to the tracker.
10. **Correlation- and chirp-aware precision sigma** — Precision measurement sigma is inflated for overlapping-window correlation and residual chirp across the 240 ms window. An optional common Doppler rate may be de-chirped before the STFT; reported frequencies are mapped back to the original AF axis.
11. **RRP-inspired short portions, not a literal MATLAB port** — fast local maxima are linked into short mutually-consistent ridge portions before long-term tracking. The Laurent/Meignen basin/spline optimizer is not copied, and SkyRoof does not impose its non-crossing spline constraint.

The design is inspired by, but does not literally implement, these published methods: Wang/Jiang/Zhang, *Random finite set approach to analyzing, detecting, and tracking dynamic time-frequency spectra* (2019, DOI 10.7527/S1000-6893.2018.22600); Meignen/Pham/McLaughlin, *On Demodulation, Ridge Detection, and Synchrosqueezing for Multicomponent Signals* (IEEE TSP 2017, DOI 10.1109/TSP.2017.2656838); Laurent/Meignen, *A Novel Ridge Detector for Nonstationary Multicomponent Signals* (IEEE TSP 2021, DOI 10.1109/TSP.2021.3085113); Meignen/Laurent/Oberlin, *One or Two Ridges? An Exact Mode Separation Condition for the Gabor Transform* (IEEE SPL 2022, DOI 10.1109/LSP.2022.3226948); and García-Fernández et al., *Bayesian Multi-Target Tracking With Merged Measurements Using Labelled Random Finite Sets* (IEEE TSP 2015, DOI 10.1109/TSP.2015.2393843).

SkyRoof deliberately uses **labelled Kalman + GNN + MergeGroup + bounded fixed-lag identity priors** rather than a full GM-PHD/GLMB filter: the problem is bounded to a small number of lanes (default 5, max 8) and the UI needs stable identities. The frame-level scanner now supplies sample-index-aligned observations and short reliable ridge portions; a fuller multi-hypothesis fixed-lag smoother remains an escalation path only if benchmarked ID-switch rates justify the added complexity.

## Quantitative acceptance and real-model benchmark

CW changes are gated by a dedicated real `deepcw-engine` ONNX benchmark in addition to unit tests. The benchmark uses deterministic synthetic Morse mixtures and oracle tracks first, so separator/model error is measured independently from detector/tracker identity error. It reports masked and unmasked CER/WER, callsign recognition and real-time factor across fixed 5/10/15/25/40 Hz separations, power imbalance, Doppler-rate cases and a >1.6 kHz wideband lane.

The frame scanner is unit-tested separately for monotonic sample timing, Fast/Precision separation, close-carrier resolution, silent-frame coast progression and Doppler de-chirp behavior. The next end-to-end corpus benchmark will add carrier detection recall/false alarms, frequency RMSE, ID switches, final CER/WER, callsign accuracy and latency. A lower ID-switch count is not accepted as a decoding improvement if CER or latency regresses materially.

## Workflow and gates

| ID | Stage | Deliverable | Acceptance gate |
|---|---|---|---|
| CW-00 | Baseline and license | CI SHA, restore path and model provenance | Existing FT4/spectrum/rotator unchanged |
| CW-01 | Receive sources | SDR slicer, WASAPI selected endpoint, RS-BA1 loopback | Reconnect/device switch without blocking |
| CW-02 | PCM pipeline | Bounded Float32 samples, UTC tags, anti-aliased resampling | Slow inference never blocks audio callback |
| CW-03 | Frame ridge scanner | Fast 80/15 ms ridge portions + Precision 240/120 ms observations, sample-index timeline, Doppler de-chirp | Fast frames never over-update Kalman; close carriers and silent coast are regression-tested |
| CW-04 | Tracking | Stable IDs, Doppler drift, QSB hold, dedup, MergeGroup/fixed-lag identity and up to 8 tracks | Crossing/merged-measurement tests preserve labelled identities |
| CW-05 | Per-lane isolation | Bandpass + smooth frequency shift to model's AF passband | Independent audio outputs for 3–5 simultaneous CW carriers |
| CW-06 | DeepCW decoding | ONNX Runtime + metadata-faithful STFT/log1p, CTC and incremental text | **Each lane** independently shows true confirmed/pending text |
| CW-07 | Load governance | Default 5, configurable up to 8 lanes, bounded inference backlog, selected-lane priority | Measured latency/CPU/RAM and safe overload behavior |
| CW-08 | HamNoise | Native CW DLL with bypass and Wet/Dry, optional monitor | Compare raw/denoised per-lane CER |
| CW-09 | Dockable UI | Waterfall, Pileup grid, selected transcript, TX editor, theme/settings | GitHub Light/Dark, pink-blue-white and layout restore |
| CW-10 | SkyCAT CW protocol | Constrained main-CAT CW_SEND/CW_ABORT/WPM; CI-V 17 / 17 FF | Unit tests for ACK, timeout, invalid text and abort |
| CW-11 | TX state machine | Idle→Armed→Queued→Sending→Stopping/Failed; macros and TX inhibit | TX disabled by default; stop clears queue; ACK not RF completion |
| CW-12 | Satellite integration | Main RX/Sub TX, CW/CW-R, CAT ownership, Doppler and mode checks | Wrong TX VFO, existing PTT, sat change inhibit TX |
| CW-13 | Release testing | Multi-carrier WAV corpus, CER/cross-talk, long soak and radio simulation | CI clean; separate real IC-9700 keyer sign-off |

**Dependency:** 00→01→02→03→04→05→06→07→09 is the complete Pileup **receive** milestone. 02→08 adds optional noise reduction; 10→11→12 introduces **transmit** only after mock-CAT tests and explicit human enabling. Only completing CW-04 does not mean the project can decode CW.

## Receive workflow

1. Capture Float32 PCM from SDR, selected Windows audio capture, or RS-BA1 playback loopback. Icom LAN Spectrum frames contain trace points, **not decodable PCM**.
2. Enter a bounded, timestamped audio hub. Optionally reduce CW noise with HamNoise; preserve a raw bypass path.
3. Run the dual-resolution ridge scanner. Fast 15 ms-hop frames form activity/reliable-ridge evidence only; Precision 120 ms-hop observations advance the tracker on the monotonic PCM sample axis. Empty precision frames still advance coast/hold time.
4. Track Precision observations using labelled Kalman/GNN/MergeGroup logic. For each active lane, extract or spectrally translate the component to DeepCW's model coordinates; classic/monitor audio may use the independent complex DDC path.
5. Run the publicly available DeepCW ONNX model with lane-specific state and CTC. Store confirmed and pending text independently; invalidate stale work when a track disappears or switches identity.
6. Render all 3–8 lane transcripts simultaneously, with AF frequency, estimated SNR, drift, state and latest call. Show a complete transcript for one selected lane. Mark unresolved co-channel collisions.
7. Track RF downlink and actual TX uplink separately through SkyRoof's existing Doppler model; do not interpret a tracked AF frequency as an uplink VFO command.

## CW Console layout specification

| Region | Contents | Interaction |
|---|---|---|
| Top toolbar, ~38px | RX Start/Stop, SDR/WASAPI/RS-BA1 source, Raw/HamNoise, Single/Pileup, model status | RX must not implicitly Arm TX |
| AF waterfall, ~150px | 100–2000Hz view, lane markers and highlights | Left click selects a lane; context menu for lock/mute |
| Pileup grid, resizable | ID, AF Hz, SNR, Hz/s, active/hold, call and per-lane transcript | Sorting never changes track identity |
| Selected RX transcript | Confirmed/pending text, UTC, copy, QSO Entry suggestion | No automatic transmit from recognition |
| TX composer, 140–200px | Message, F1–F8 macros, WPM, break-in, Arm/Send and always-visible Abort | Explicit preflight + operator action |
| Status strip, ~22px | Audio/model latency, active lanes, CPU, CAT ownership/errors | Accessible diagnostics and fallback |

Implement as SkyRoof/Panels/CwConsolePanel.cs using existing DockContent, Context, View menu, layout persistence and Theme palette. Do not lift React JSX. Persist RX choices, macros, tracking parameters and splitter layout but **never** persist TX armed state.

## Safe text CW keyer workflow

SkyCAT remains sole owner of the RS-BA1 virtual CI-V serial port, using its existing main port 4532 and shared command lock. Add an explicit CW command whitelist, not a general raw CI-V tunnel. Keep auxiliary port 4537 restricted to non-transmit settings. IC-9700 CI-V 17 sends at most 30 ASCII characters per frame and 17 FF aborts queued keying; a successful command ACK is **not** proof that the complete text finished transmitting.

Require supported radio, correct SAT TX receiver, CW/CW-R mode, licensed frequency and unoccupied PTT ownership before explicit Arm+Send. Stop has priority over unsent queue entries. On link loss clear queued messages, do not resume automatically, and display **TX status unknown** if physical abort cannot be confirmed. Never automatically reply to a decoded callsign.

## Verification

Use synthetic and recorded simultaneous CW WAV at 10/20/30/40 WPM; 3/5/8 carriers; varied SNR; AWGN, QSB, nearby interferers, 1/10/30Hz spreading and 0–20Hz/s Doppler. Measure **per-lane character error rate**, mis-association, cross-talk, candidate false alarms, dropout, end-to-end latency, CPU and RAM. Verify long runtime, audio device reconnects, mock CI-V ACK/rejection/timeout, abort and FT4/CAT/spectrum/rotator regressions. Never claim RF TX success without a supervised IC-9700 bench test.

## Proposed PR sequence

1. Track manager + multi-lane DeepCW core + reliability fixes and real-model benchmark (**merged in PR #41/#42**).
2. Dual-resolution Frame Ridge Scanner, sample-index timeline and precision-only Kalman updates (**PR #43**).
3. Incremental CTC/transcript reconciliation plus live SDR/WASAPI/RS-BA1 PCM wiring.
4. Dockable CW Console UI, load governance, HamNoise and settings.
5. SkyCAT constrained CW protocol and mock serial tests.
6. Safe TX + satellite CAT integration and supervised bench verification.
7. End-to-end WAV corpus metrics, bilingual user guide, license audit and release checks.
