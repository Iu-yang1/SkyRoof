# CW Console: full multi-lane Pileup and CI-V keyer implementation plan

**Status: engineering plan and tracking foundation only.** This branch adds a carrier track manager and unit tests. It does **not** yet ship an audio source, neural decoder, real Pileup text decoding, CW Console UI, noise reducer, or an enabled radio transmitter.

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
5. **Ridge-aware soft TF masks** — each lane receives a Gaussian/Wiener-like mask along its predicted ridge, normalized against competing lanes, then translated to the DeepCW ~800 Hz center. Mask width combines intrinsic CW TF width with Kalman frequency uncertainty.
6. **Shared STFT, independent inference** — resampling/STFT happens once for the receive window; masks and DeepCW+CTC are lane-specific.

The design is inspired by, but does not literally implement, these published methods: Wang/Jiang/Zhang, *Random finite set approach to analyzing, detecting, and tracking dynamic time-frequency spectra* (2019, DOI 10.7527/S1000-6893.2018.22600); Meignen/Pham/McLaughlin, *On Demodulation, Ridge Detection, and Synchrosqueezing for Multicomponent Signals* (IEEE TSP 2017, DOI 10.1109/TSP.2017.2656838); Laurent/Meignen, *A Novel Ridge Detector for Nonstationary Multicomponent Signals* (IEEE TSP 2021, DOI 10.1109/TSP.2021.3085113); Meignen/Laurent/Oberlin, *One or Two Ridges? An Exact Mode Separation Condition for the Gabor Transform* (IEEE SPL 2022, DOI 10.1109/LSP.2022.3226948); and García-Fernández et al., *Bayesian Multi-Target Tracking With Merged Measurements Using Labelled Random Finite Sets* (IEEE TSP 2015, DOI 10.1109/TSP.2015.2393843).

SkyRoof deliberately uses **labelled Kalman + GNN + merge/coast handling** rather than a full GM-PHD/GLMB filter: the problem is bounded to a small number of lanes (default 5, max 8) and the UI needs stable identities. MHT/GLMB or fixed-lag multi-hypothesis smoothing remains an escalation path if dense-clutter benchmarks show that GNN is insufficient.

## Workflow and gates

| ID | Stage | Deliverable | Acceptance gate |
|---|---|---|---|
| CW-00 | Baseline and license | CI SHA, restore path and model provenance | Existing FT4/spectrum/rotator unchanged |
| CW-01 | Receive sources | SDR slicer, WASAPI selected endpoint, RS-BA1 loopback | Reconnect/device switch without blocking |
| CW-02 | PCM pipeline | Bounded Float32 samples, UTC tags, anti-aliased resampling | Slow inference never blocks audio callback |
| CW-03 | CW detector | STFT spectral candidates, local floor, SNR hysteresis, temporal CW discrimination | Multiple actual CW candidates, suppress stationary carriers |
| CW-04 | Tracking | Stable IDs, Doppler drift, QSB hold, dedup and up to 8 tracks | **This PR**: deterministic track manager and tests |
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
3. Detect candidate audio tones and CW-like temporal envelopes. Track them using stable IDs through modest Doppler drift and fading.
4. For each active lane, apply its own BPF and NCO to map tone to 400–1200 Hz. Resample to the decoder model sample rate.
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

1. Track manager, tests, bilingual engineering specification (**this PR**).
2. Bounded PCM capture and CW signal detector.
3. **Complete per-lane DeepCW** BPF/NCO/inference/CTC with multi-carrier WAV benchmark.
4. Dockable UI, HamNoise and settings.
5. SkyCAT constrained CW protocol and mock serial tests.
6. Safe TX + satellite CAT integration and bench verification.
7. Bilingual user guide, license audit and release checks.
