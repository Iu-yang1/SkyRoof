# CW Console: full multi-lane Pileup and CI-V keyer implementation plan

**Status: the receive chain, safe Command-17 TX, satellite TX interlock, and F1–F8 message presets are implemented.** The repository now contains multi-carrier detection/tracking, dual-resolution ridge scanning, bounded fixed-lag association, multi-lane DeepCW/CTC, incremental transcripts, live SDR/WASAPI/RS-BA1-loopback PCM, the non-blocking worker, AF waterfall, an explicitly armed IC-9700 Command-17 text keyer, and persistent F1–F8 macros. F1–F8/buttons only load a preset into the composer; **Shift+F1…Shift+F8** explicitly sends only after the operator has already enabled and armed TX, reusing TXHZ/SENDHZ, satellite interlock, watchdog, STOP/disconnect and all existing fail-safes. SkyRoof still does not automatically select CW/CW-R, enable BK-IN, auto-reply to decoded callsigns, choose TX frequency from an RX lane, or maintain a macro queue. HamNoise remains benchmark-only.

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
8. **MergeGroup fallback** — when finite resolution collapses two labelled ridges into one peak, the tracker still preserves pre-merge ridge anchors and inflates covariance during the shared-peak interval. This remains a short local safety layer beneath the multi-frame associator.
9. **Dual-resolution frame scanner** — Fast STFT uses 80 ms / 15 ms and is activity/continuity evidence only; its observations are explicitly not eligible for Kalman updates. Precision STFT uses 240 ms / 120 ms, reports sample-index aligned frequency/SNR/resolution/measurement sigma/activity, and is the only frame stream sent to the tracker.
10. **Correlation- and chirp-aware precision sigma** — Precision measurement sigma is inflated for overlapping-window correlation and residual chirp across the 240 ms window. An optional common Doppler rate may be de-chirped before the STFT; reported frequencies are mapped back to the original AF axis.
11. **True fixed-lag multi-frame association** — before Kalman/GNN, Precision batches pass through a bounded beam/MHT that retains 3 future batches by default (~360 ms). Global hypotheses accumulate frequency-innovation, local-velocity, velocity-change, SNR, activity and ridge-portion continuity costs. Only after the lag expires does the oldest batch receive stable `AssociationHintId` labels. New paths need at least two observations of future support, so one-frame clutter cannot immediately create a track.
12. **RRP-inspired short portions, not a literal MATLAB port** — fast local maxima are linked into short mutually-consistent ridge portions before long-term tracking. The Laurent/Meignen basin/spline optimizer is not copied, and SkyRoof does not impose its non-crossing spline constraint.

The design is inspired by, but does not literally implement, these published methods: Wang/Jiang/Zhang, *Random finite set approach to analyzing, detecting, and tracking dynamic time-frequency spectra* (2019, DOI 10.7527/S1000-6893.2018.22600); Meignen/Pham/McLaughlin, *On Demodulation, Ridge Detection, and Synchrosqueezing for Multicomponent Signals* (IEEE TSP 2017, DOI 10.1109/TSP.2017.2656838); Laurent/Meignen, *A Novel Ridge Detector for Nonstationary Multicomponent Signals* (IEEE TSP 2021, DOI 10.1109/TSP.2021.3085113); Meignen/Laurent/Oberlin, *One or Two Ridges? An Exact Mode Separation Condition for the Gabor Transform* (IEEE SPL 2022, DOI 10.1109/LSP.2022.3226948); and García-Fernández et al., *Bayesian Multi-Target Tracking With Merged Measurements Using Labelled Random Finite Sets* (IEEE TSP 2015, DOI 10.1109/TSP.2015.2393843).

13. **Incremental CTC transcript reconciliation** — DeepCW exposes each emitted character's CTC `OutputFrame`, output-frame count and a top-vs-runner-up confidence. Overlapping decode windows are sequence-aligned on approximate absolute symbol time and confidence-weighted before text is committed. Each lane has an immutable `CommittedText` prefix plus a correctable `ProvisionalText` suffix. `AssociationHintId` is the preferred transcript identity, so text remains attached to the same multi-frame path through TrackId rebuilds and crossings.

SkyRoof now uses **Frame Ridge Scanner → bounded fixed-lag beam/MHT → labelled Kalman/GNN → MergeGroup fallback** rather than a full GM-PHD/GLMB filter. The default associator retains only about 360 ms of future Precision observations and a bounded hypothesis beam, which is appropriate for the 5–8 lane desktop problem while still resolving crossings with future evidence. Heavier MHT/GLMB remains an escalation path only if end-to-end ID-switch benchmarks justify it.

## Quantitative acceptance and real-model benchmark

CW changes are gated by a dedicated real `deepcw-engine` ONNX benchmark in addition to unit tests. The benchmark uses deterministic synthetic Morse mixtures and oracle tracks first, so separator/model error is measured independently from detector/tracker identity error. It reports masked and unmasked CER/WER, callsign recognition and real-time factor across fixed 5/10/15/25/40 Hz separations, power imbalance, Doppler-rate cases and a >1.6 kHz wideband lane.

The frame scanner is unit-tested separately for monotonic sample timing, Fast/Precision separation, close-carrier resolution, silent-frame coast progression and Doppler de-chirp behavior. Incremental transcript reconciliation is additionally exercised by a real-ONNX 40 Hz two-lane sliding-window benchmark (6 s windows / 1 s hop) that records naive concatenation CER versus stable streaming CER/WER. The next end-to-end corpus benchmark will add carrier detection recall/false alarms, frequency RMSE, ID switches, final CER/WER, callsign accuracy and latency. A lower ID-switch count is not accepted as a decoding improvement if CER or latency regresses materially.

**PR #50 HamNoise real-model A/B (Wet=1, pinned HamNoise `1af3a77b...` / DeepCW `8e264d24...`):** the current wideband soft-mask baseline measures mean CER/WER **0.2273 / 0.2381**, **18/28** full callsigns and mean RTF **0.0618**. On the same per-lane DDC architecture, LaneDry is CER **0.4513**; Classic improves it to **0.4221** and V2 to **0.3669**, proving HamNoise has a net denoise benefit there, but neither recovers the loss from removing the all-track soft mask, and per-lane V2 is slower than real time at mean RTF **2.1088**. A second placement denoises the immutable decode snapshot once *after tracking* and then keeps the existing all-track soft mask: Shared Classic still regresses to CER **0.3896** / **8/28** callsigns; Shared V2 matches baseline CER at **0.2273** and remains real-time at mean RTF **0.4260**, but worsens WER to **0.3214** and callsigns to **15/28**, so it is still not exposed in UI or releases. A post-hoc rule selecting Shared V2 only when nearest-neighbour spacing is ≤10 Hz yields CER **0.1981** with baseline WER **0.2381** and callsigns **18/28** on these 14 synthetic cases, but that is only a research hypothesis; it requires an independent spacing/power/Doppler grid and recorded corpus before any automatic enablement.

## Workflow and gates

| ID | Stage | Deliverable | Acceptance gate |
|---|---|---|---|
| CW-00 | Baseline and license | CI SHA, restore path and model provenance | Existing FT4/spectrum/rotator unchanged |
| CW-01 | Receive sources | SDR Slicer, WASAPI capture, RS-BA1 render-endpoint loopback, source-isolated timeline, disabled-by-default receive | CI source-isolation/switch/clock rollback tests; live device rebinding without blocking audio callbacks |
| CW-02 | PCM pipeline | Bounded Float32 samples, UTC tags, anti-aliased resampling | Slow inference never blocks audio callback |
| CW-03 | Frame ridge scanner | Fast 80/15 ms ridge portions + Precision 240/120 ms observations, sample-index timeline, Doppler de-chirp | Fast frames never over-update Kalman; close carriers and silent coast are regression-tested |
| CW-04 | Multi-frame association + tracking | 3-batch / ~360 ms beam-MHT, AssociationHintId, Kalman/GNN, MergeGroup fallback, up to 8 tracks | Crossing, single-peak merge, one-frame clutter and ID-swap regressions |
| CW-05 | Per-lane isolation | Bandpass + smooth frequency shift to model's AF passband | Independent audio outputs for 3–5 simultaneous CW carriers |
| CW-06 | DeepCW + continuous transcript | ONNX Runtime + metadata-faithful STFT/log1p/CTC; OutputFrame timing; committed/provisional text; AssociationHintId ownership | Overlap de-duplication, pre-commit correction, repeated-character and per-lane isolation tests |
| CW-07 | Load governance | Independent 120 ms tracker cadence; 6 s DeepCW snapshots / 1 s hop; latest-only single inference; default 5 lanes | Busy inference skips old hops instead of queueing; tracker remains independent; completed/skipped windows are observable |
| CW-08 | HamNoise research | Pinned-revision Classic/CW V2 native bridge, per-lane DDC and shared decode-window placements, LaneDry/raw controls | Dedicated HamNoise workflow compares CER/WER/callsign/RTF; current Shared V2 only matches CER while regressing WER/callsigns, so no UI/release exposure |
| CW-09 | Dockable UI | RX controls + Pileup/transcript/waterfall plus an isolated TX composer | UI owns no tracker/ONNX resources; closing it leaves RX alive but disarms/stops CW TX started by the Console |
| CW-10 | SkyCAT CW protocol | Dedicated loopback `4538` PING/CAPS/STATUS/SEND/STOP; CI-V 17 / 17 FF; 16 47 BK-IN; 14 0C KEYRAW | 30-char/alphabet, mode/BK-IN/TX preflight, shared PTT lease, timeout/disconnect fail-safe tests (SkyCAT PR #27) |
| CW-11 | TX state machine | Persistent enable (default off) + non-persistent Arm; Send/STOP; KEYRAW→WPM duration watchdog; connection teardown secondary fail-safe | unenabled/unarmed sends rejected; Console/app close STOPs; ACK is never treated as RF completion (SkyRoof PR #51) |
| CW-12 | Satellite TX interlock | Arm captures satellite/transmitter/no-Doppler uplink/mode/transverter; TXHZ + SENDHZ actual-VFO guard; freeze SkyRoof TX CAT writes; CW lease blocks other TX-side CAT writes | Wrong TX VFO, existing PTT, sat/transmitter/uplink/mode/transverter changes reject or abort TX; normal Doppler motion does not false-trigger |
| CW-13 | Release testing | Multi-carrier WAV corpus, CER/cross-talk, long soak and radio simulation | CI clean; separate real IC-9700 keyer sign-off |

**Dependency:** 00→01→02→03→04→05→06→07→09 is the complete Pileup **receive** milestone. 02→08 adds optional noise reduction; 10→11→12 introduces **transmit** only after mock-CAT tests and explicit human enabling. Only completing CW-04 does not mean the project can decode CW.

## Receive workflow

1. Capture Float32 PCM from the existing 48 kHz SDR Slicer, a selected Windows WASAPI capture endpoint, or RS-BA1 render-endpoint loopback. Loopback is downmixed and resampled to mono 48 kHz before the shared ingress. Icom LAN Spectrum frames contain trace points, **not decodable PCM**.
2. Route exactly one configured source through `CwPcmIngress`; changing source or seeing a backwards wall-clock timestamp starts a clean sample timeline. RS-BA1 loopback captures the whole render-endpoint mix rather than a process-isolated stream, so a dedicated endpoint is recommended. Then enter the bounded, timestamped audio hub. **Do not denoise before carrier detection/tracking.** HamNoise exists only in benchmark decode branches after raw tracking: either DDC one selected track into a 9.6 kHz lane, or resample the immutable decode snapshot once to 9.6 kHz and retain the normal all-track soft mask. The production path remains raw bypass.
3. Run the dual-resolution ridge scanner. Fast 15 ms-hop frames form activity/reliable-ridge evidence only; Precision 120 ms-hop observations remain on the monotonic PCM sample axis.
4. Hold Precision batches in the bounded fixed-lag beam/MHT for about 360 ms, commit the oldest batch with future-validated AssociationHintId labels, then update the labelled Kalman/GNN/MergeGroup tracker. Empty batches follow the same delayed timeline and still advance coast/hold time. For each active lane, extract or spectrally translate the component to DeepCW's model coordinates; classic/monitor audio may use the independent complex DDC path.
5. Run carrier tracking on its own 120 ms worker. A separate latest-only DeepCW lane takes immutable 6 s snapshots at a nominal 1 s hop; if the previous inference is still running, that hop is skipped rather than queued. Each inference is tagged with the current TimelineGeneration so results from a previous device/source are discarded. Map emitted OutputFrames to approximate absolute time, reconcile overlapping windows by sequence-constrained symbol voting, and expose immutable committed text plus a correctable provisional suffix.
6. Render all 3–8 lane transcripts simultaneously, with AF frequency, estimated SNR, drift, state and latest call. Show a complete transcript for one selected lane. Mark unresolved co-channel collisions.
7. Track RF downlink and actual TX uplink separately through SkyRoof's existing Doppler model; do not interpret a tracked AF frequency as an uplink VFO command.

## CW Console layout specification

| Region | Contents | Interaction |
|---|---|---|
| Top toolbar | RX Start/Stop, SDR/WASAPI/RS-BA1 source, Settings, DeepCW model install/status | RX remains resource/state-isolated from TX |
| AF waterfall, ~180px | Current Frame Scanner AF range, 250 Hz scale, stable H/T lane labels, Active/Hold/Ambiguous markers and ±2σ frequency covariance bands | Left click selects an existing lane only; it does not tune the radio or alter AF/RF |
| Pileup grid, resizable | ID, AF Hz, SNR, Hz/s, active/hold, call and per-lane transcript | Sorting never changes track identity |
| Selected RX transcript | Confirmed/pending text, UTC, copy, QSO Entry suggestion | No automatic transmit from recognition |
| TX composer, ~208px | Max-30-char message, non-persistent Arm/Disarm, Send, always-visible red STOP, mode/BK-IN/WPM/watchdog, F1–F8 macro buttons | F1–F8 only load; Shift+F1–F8 explicitly send; Settings enable + Arm still required; no automatic reply or macro queue |
| Status strip, ~22px | Audio/model latency, active lanes, CPU, CAT ownership/errors | Accessible diagnostics and fallback |

Implement as SkyRoof/Panels/CwConsolePanel.cs using existing DockContent, Context, View menu, layout persistence and Theme palette. Do not lift React JSX. Persist RX choices, macros, tracking parameters and splitter layout but **never** persist TX armed state.

## Safe text CW keyer workflow

SkyCAT keeps the existing 4532 CAT and 4537 Remote Control Switch responsibilities and exposes CW on a separate **loopback-only 4538 whitelist endpoint**, never a raw CI-V tunnel. `STATUS` reports mode/BK-IN/TX/KEYRAW and, in the satellite-aware protocol, the radio's actual TX frequency as `TXHZ`. Satellite clients use `SENDHZ <expectedHz> <toleranceHz> <text>`; SkyCAT re-reads the TX VFO inside the same serial/lease critical section immediately before Command 17 and returns `ERR FREQ` on mismatch, **validating only and never retuning**. While the CW lease is active, the normal CAT endpoint fail-closes TX frequency/mode/CTCSS/operating-mode writes while allowing RX frequency/mode and read-only traffic. The existing CW/CW-R, Semi/Full BK-IN, hardware-idle, and shared PTT-lease checks remain mandatory. IC-9700 Command 17 sends at most 30 supported characters and binary 17 FF stops it; ACK is not proof of completion. SkyRoof still uses 14 0C KEYRAW for the watchdog, and disconnect/reconnect/shutdown remain secondary STOP paths.

The satellite stage adds validation without taking tuning authority away from the operator. SkyRoof does **not** derive or set a TX frequency from a selected decoded lane. It validates the existing RadioLink satellite context instead: linear uplinks must remain inside the base-corrected published passband; a single-frequency uplink is limited to the corrected base ±5 kHz; transverter CAT mapping must remain unchanged; and SkyCAT must report an actual TX VFO within the SENDHZ tolerance. Normal Doppler evolution is allowed and continues to be computed while SkyRoof's own TX CAT writes are frozen; after STOP the next normal tuning tick catches up. SkyRoof's coarse 2 m / 70 cm check is a software sanity bound, **not a determination of legal authority under the operator's jurisdiction or license class**. STOP retains priority, link loss is never auto-resumed, and decoded callsigns are never transmitted automatically.

## Verification

Use synthetic and recorded simultaneous CW WAV at 10/20/30/40 WPM; 3/5/8 carriers; varied SNR; AWGN, QSB, nearby interferers, 1/10/30Hz spreading and 0–20Hz/s Doppler. Measure **per-lane character error rate**, mis-association, cross-talk, candidate false alarms, dropout, end-to-end latency, CPU and RAM. Verify long runtime, audio device reconnects, mock CI-V ACK/rejection/timeout, abort and FT4/CAT/spectrum/rotator regressions. Never claim RF TX success without a supervised IC-9700 bench test.

## Proposed PR sequence

1. Track manager + multi-lane DeepCW core + reliability fixes and real-model benchmark (**merged in PR #41/#42**).
2. Dual-resolution Frame Ridge Scanner, sample-index timeline and precision-only Kalman updates (**PR #43**).
3. Bounded fixed-lag beam/MHT, multi-frame crossing/merge association and AssociationHintId (**PR #44**).
4. Incremental CTC timing, overlapping-window voting and real-ONNX streaming benchmark (**PR #45**).
5. Live SDR / WASAPI capture / RS-BA1 render-endpoint loopback PCM wiring, hot settings rebinding and source timeline isolation (**PR #46**).
6. Decoupled tracker/ONNX receive worker, latest-only inference and TimelineGeneration stale-result suppression (**PR #47**).
7. RX-only dockable CW Console: source/model/worker status, Pileup lane grid, selected committed/provisional transcript and docking restore (**PR #48**).
8. CW AF waterfall, stable lane overlays, ±2σ uncertainty bands and an independent ~10 FPS display cadence (**PR #49**).
9. HamNoise benchmark-only experiment with pinned native bridge, per-lane/shared Classic and CW V2 placements, LaneDry control, and real-model A/B; also fixes long-window Int32 overflow in CwLaneExtractor/CwWindowedSincResampler (**PR #50**).
10. Dedicated SkyCAT 4538 CW whitelist protocol, shared PTT lease and disconnect/reconnect/shutdown fail-safe (**SkyCAT PR #27**).
11. SkyRoof two-gate TX arming, RS-BA1-style text composer, KEYRAW watchdog and close/shutdown STOP (**SkyRoof PR #51**).
12. Satellite TX interlock: TXHZ/SENDHZ, satellite/transmitter/no-Doppler-uplink/mode/transverter context lock, SkyRoof TX-CAT freeze, active-change STOP+Disarm, and SkyCAT CW-lease TX-side CAT write gate (**SkyCAT PR #28/#29; SkyRoof PR #52**).
13. Persistent F1–F8 CW message presets: normal F keys/buttons load only; Shift+F1–F8 explicitly sends through the complete TX state machine; no auto-reply or macro queue (**SkyRoof PR #53**).
14. Supervised IC-9700 RF sign-off: dummy load / low power first; verify CW/CW-R, BK-IN, actual uplink, Doppler catch-up, macro Shift-send and STOP. Software checks do not replace local regulatory/license requirements.
15. End-to-end WAV corpus metrics, bilingual user guide, license audit and release checks.