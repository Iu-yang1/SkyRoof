# CW Console

The CW Console is SkyRoof's receive-and-keyer panel for simultaneous CW signals.
It combines multi-carrier tracking, DeepCW text decoding, a live AF waterfall,
continuous per-lane transcripts, and an operator-initiated IC-9700 Command-17
text keyer with per-send preflight.

The receive and transmit sides are intentionally separated. Decoded text never
automatically selects a transmit frequency and is never transmitted
automatically.

## Open the panel

Open **View > CW Console**. Closing the panel does not stop the background CW
receiver, but it always stops/disarms CW transmit.

The panel is a normal DockContent window and can be docked with the other
SkyRoof panels.

## Receive audio source

Choose one receive source in the CW Console:

- **SkyRoof SDR audio** — reuses SkyRoof's existing 48 kHz Slicer AF audio.
- **IC-9700 USB AF input** — opens a Windows **capture/input** endpoint.
  New configurations prefer an endpoint whose name contains **USB Audio CODEC**
  or **ICOM**. For an IC-9700 this is normally **Microphone (USB Audio CODEC)**,
  but the radio's **USB AF/IF Output must be AF**. Do not use this path when the
  radio is supplying IF instead of demodulated AF.
- **RS-BA1 playback loopback** — captures a selected Windows **playback/render**
  endpoint in loopback mode, downmixes it to mono, and resamples it to 48 kHz.
  Select the speaker/virtual endpoint that RS-BA1 is actually playing into.

RS-BA1 loopback captures the **whole render-endpoint mix**, not only the
Remote Utility process. A dedicated playback endpoint is recommended when you
want only RS-BA1 receiver audio.

Changing the source or changing the actual audio-device ID starts a new CW
timeline. Tracks and transcript state from the previous device are discarded so
two sources cannot be mixed into one lane history.

CW receive is disabled by default. Use **Start** in the panel after selecting
the intended source.

As a practical rule: use **IC-9700 USB AF input** only for direct USB AF from
the radio; use **RS-BA1 playback loopback** for an RS-BA1/LAN receive workflow.
The status line shows the concrete Windows endpoint that is currently open.

## CW Skimmer V1 horizontal workspace

The CW Console is organized around a **wide left AF spectrum and waterfall,
right-side Pileup messages, a compact selected-lane RX readout, and a permanent
bottom transmit strip**. The old seven-column Lane DataGridView is no longer
part of the main window.

- **Top:** Start/Stop RX, audio source, display-only Raw/HamNoise selector,
  settings and decode/worker status.
- **Left:** a **vertical AF frequency ruler on the left**, a narrow vertical
  live spectrum trace, and the main high-contrast waterfall. Every incoming
  spectrum frame adds a time column at the **right**; history scrolls **right
  to left** as in CW Skimmer. H/T lane markers are horizontal frequency lines.
  Drag the splitter to change the spectrum/message ratio.
- **Right:** eight stable, vertically scrollable Pileup cards. Each shows
  Lane ID, AF Hz, SNR and state above a **word-wrapped, selectable and
  independently scrollable transcript**; provisional text is colored
  separately. Long CW messages can be read without clipping, and each
  card retains a Copy button. A card stays in its numbered slot through short QSB/Hold
  and frequency crossings. Click a card or waterfall marker to select the
  same lane in both places.
- **Immediately above CW TX:** a compact **CW RX — Selected Lane** GroupBox
  using the same styling as the transmit area. Clicking a Pileup card or a
  waterfall lane marker updates its slot number, stable H/T identity, AF Hz,
  SNR and Active/Hold/Ambiguous/Grace state. The full selected receive text
  wraps and scrolls in a read-only field; uncommitted characters are colored
  separately. **Copy RX** copies the raw transcript without formatting.
  If the lane disappears or the audio timeline resets, stale text is cleared.
- **Bottom:** Send (automatic preflight), prominent STOP, one-line keyer composer,
  verified WPM set/readback, and F1-F8 macro keys. Action/macro lines remain
  individually horizontally scrollable in narrower windows.

The RX detail is **receive-only**: selecting a lane or copying decoded text
cannot send CW, adjust a TX VFO or override the satellite interlock. It
updates from the existing 250-ms UI snapshot rather than starting a new
decoder or FFT worker.

**AF navigation:** use the **vertical scrollbar on the right of the waterfall**
to pan the visible frequency range up/down and Zoom for 1-8x magnification.
Ctrl+mouse-wheel zooms and ordinary mouse-wheel pans the **vertical AF axis**.
The horizontal waterfall movement is **time history**, not radio tuning.
All navigation is **display only** and never alters frequency tuning, raw PCM,
DeepCW inference, tracking, or TX safety interlocks. HamNoise remains
optional display-only processing.

## Install the DeepCW model

The CW Console downloads DeepCW only after explicit operator action.

SkyRoof pins the model to the immutable revision:

`8e264d243bbd4467bd19f3f28292219405b47e0e`

The model is provided by
[e04/deepcw-engine](https://github.com/e04/deepcw-engine) under
AGPL-3.0-only. It is not included in the SkyRoof installer.

The model-status area shows whether the model is installed and whether ONNX
inference is currently available.

## Pileup receive chain

The production receive path is:

`PCM -> dual-resolution ridge scanner -> ~360 ms fixed-lag association -> labelled Kalman/GNN tracker -> activity-aware all-track separation -> DeepCW ONNX/CTC -> incremental transcript`

Tracking runs independently from ONNX inference. The tracker normally advances
about every 120 ms. DeepCW uses a rolling snapshot and a nominal 1 s decode hop.
If an inference is still busy when the next hop arrives, SkyRoof skips the old
window instead of building an unbounded queue.

### Lane identity

The Pileup cards and waterfall prefer a stable fixed-lag association ID:

- **H123** — stable AssociationHint identity.
- **T17** — transient TrackId fallback when no association hint exists yet.

A lane can therefore keep the same visible identity through a frequency
crossing even if the low-level tracker instance changes.

### CW Skimmer-style spectrum and stable lane slots

The CW display uses a dark, high-contrast CW-Skimmer-style spectrum/waterfall:
a slim live spectrum trace is drawn beside the vertical frequency ruler,
new time columns scroll from right to left, and the noise floor remains
dark instead of being stretched to full brightness every frame, and narrow CW
carriers progress from green toward yellow/white as they become stronger.
Horizontal frequency grid lines continue across the live trace and waterfall.

Track markers are deliberately lighter than before. Only the **selected** lane
shows a full-height ±2 sigma uncertainty band, so diagnostic overlays no longer
hide the real spectrum.

The right-hand Pileup panel uses eight stable numbered lane cards. A lane
keeps its card while the AssociationHint/Track identity survives; a short
Hold/dropout enters a grace state instead of shifting the other cards. A slot
is released only after the lane has really disappeared for several seconds.

Clicking a lane overlay selects that existing lane. It does not tune the radio
and does not change AF or RF frequency.

## Continuous transcript

Each lane has two text regions internally:

- **Committed** text — stable prefix that no longer changes.
- **Provisional** text — recent suffix that can still be corrected by later
  overlapping windows.

SkyRoof aligns CTC output-frame timing across overlapping decode windows and
uses repeated evidence before committing text. This prevents simple
window-to-window concatenation from repeating the same characters.

A recognized callsign or provisional text is receive information only. It never
causes an automatic reply.

## Enable CW transmit

CW transmit is permitted by **Settings > CW Console > Enable CW
Transmit**. There is no longer an Arm TX button: clicking **Send** or
**Shift+F1..F8** is the explicit operator action. The Console captures the
current satellite/transmitter context and prepares the internal interlock
immediately before that message. This state is never persisted.

SkyRoof does not automatically:

- select CW or CW-R;
- enable Semi/Full BK-IN;
- assert PTT for this keyer path;
- choose a transmit frequency from a decoded receive lane.

CW transmit capability is enabled by default, but this **does not key the
radio automatically**. RX text never triggers TX. Every explicit Send must
pass the CW/CW-R, Semi/Full BK-IN, SkyCAT lease and frequency-interlock checks.

Before sending, configure the radio yourself for CW/CW-R and Semi/Full BK-IN.

## SkyCAT keyer connection

The text keyer uses SkyCAT's dedicated loopback-only Command-17 endpoint,
normally:

`127.0.0.1:4538`

SkyCAT owns the hardware lease. It verifies the radio state and serializes the
keyer against other transmit users. A disconnect is also a fail-safe: SkyCAT
issues Command 17 `FF` STOP when the keyer client disappears.

The transmit area provides a **6-48 WPM** control. **Set WPM** sends SkyCAT
`SETWPM`, which writes IC-9700 CI-V `14 0C` and only succeeds after an
immediate matching hardware readback. While TX is armed but idle, the Console
refreshes `STATUS` at about 1 Hz while internally prepared and idle;
front-panel KEY SPEED changes appear in the Console after a send.

The CW Console displays the verified actual key-speed-derived WPM and the
watchdog countdown while a message is active.

## Satellite transmit interlock

Satellite CW sends add another safety layer.

At the explicit send action, SkyRoof captures the selected satellite/transmitter,
no-Doppler uplink position, uplink mode, and transverter mapping. Immediately
before a satellite message:

- SkyRoof freezes only its own TX CAT frequency/mode/CTCSS writes;
- RX and Doppler calculation continue;
- SkyCAT reads the actual TX VFO;
- the message uses atomic `SENDHZ expectedHz toleranceHz text`.

If the actual TX VFO is outside the permitted tolerance, the message is
rejected before Command 17.

During an active message, changing the satellite, transmitter, operator
no-Doppler tuning position, uplink mode, or transverter context causes STOP and
Disarm. Normal Doppler evolution is allowed.

For a linear transponder, the uplink must remain inside the corrected published
uplink passband. For a single-frequency uplink, SkyRoof applies a bounded
corrected-base safety window. These software checks are **not** a legal or
license-authority determination; the operator remains responsible for local
regulations and privileges.

## Send a message

The composer accepts at most 30 Command-17 characters.

Press **Send** to prepare the interlock and run the complete radio
preflight immediately before each message. The message lease remains open
until STOP or watchdog completion.

The watchdog estimates Morse duration from the IC-9700 KEYRAW key-speed value,
adds a safety margin, and sends STOP if the message does not finish within the
bounded interval.

The red **STOP** button is independent of message composition and should be
used whenever the transmit state is uncertain.

## F1-F8 message macros

**Right-click any F1-F8 button** to edit its preset in place and save it
immediately to Settings.json. You can also edit presets under
**Settings > CW Console > CW Message Macros**. The defaults are empty.

- **F1 ... F8** or clicking a macro button: load the preset into the composer
  only.
- **Shift+F1 ... Shift+F8**: explicitly send that preset through the complete
  TX safety state machine.
- Ctrl/Alt-modified function keys are not captured as CW transmit shortcuts.

Keyboard auto-repeat cannot queue multiple macro sends while the first
asynchronous TX preflight is still running.

Macros do not bypass Enable TX, CW/BK-IN checks, the satellite TXHZ/SENDHZ
guard, the lease, watchdog, STOP, or disconnect fail-safe.

There is no macro queue and no auto-reply.

## Optional HamNoise spectrum cleanup

The installer includes the pinned AGPL HamNoise backend for **display only**.
The CW Console Spectrum selector provides:

- **Raw** — unprocessed display PCM;
- **HamNoise Classic**;
- **HamNoise CW V2**.

HamNoise is applied only to an immutable copy used by the live spectrum and
waterfall. The ridge detector, fixed-lag tracker, DeepCW model, transcript
coordinator and TX logic always receive the untouched receive PCM. Therefore
changing Spectrum cleanup can change what the operator sees, but cannot change
which lanes are detected or what DeepCW decodes.

To reduce false decodes independently from display cleanup, the production
decoder also requires local spectral prominence and real CW on/off keying
evidence before sending a confirmed track to ONNX, and rejects low-margin CTC
symbols before transcript voting.

## First RF test

Perform the first transmit test on a dummy load or otherwise controlled,
low-power setup.

Recommended sequence:

1. disable/verify RF as appropriate before changing settings;
2. set the IC-9700 TX VFO to CW or CW-R yourself;
3. enable Semi/Full BK-IN yourself;
4. verify the expected satellite/transverter TX VFO shown in the Console;
5. enable **CW Transmit** in Settings;
6. press Send for a very short test message (automatic preflight);
8. press STOP and verify immediate keying termination;
9. verify Doppler catch-up after the lease releases;
10. only then proceed to an on-air test that is permitted by your licence and
    local band plan.

## Recorded-audio regression benchmark

SkyRoof also includes an offline recorded-WAV corpus runner for regression
testing the whole receive chain. See
[Recorded CW corpus](https://github.com/Iu-yang1/SkyRoof/tree/master/benchmarks/cw-recorded-corpus).

The truth reference frequency is used only after decoding to score lane
identity; it is not supplied to detection or tracking.

## Troubleshooting

### Model not installed

Use the model-install control in the CW Console. Check network access to the
pinned deepcw-engine revision.

### No lanes appear

Check that CW receive is started, the correct audio source is selected, and the
CW AF tones fall inside the scanner range. For RS-BA1 loopback, verify that
Remote Utility audio is actually routed to the selected render endpoint.

### Text appears on the wrong lane

Look at the H/T identity, Ambiguous state and ±2 sigma band. Strong overlap,
merged peaks or very small carrier spacing can temporarily increase identity
uncertainty.

### Send preflight fails

Verify all of the following:

- **Enable CW Transmit** is on;
- SkyCAT keyer endpoint is reachable on the configured port;
- the radio TX VFO is already CW/CW-R;
- Semi/Full BK-IN is already enabled;
- no other transmit lease is active;
- for satellite operation, the selected transmitter/uplink context is valid.

### Satellite send reports TX VFO mismatch

Do not increase the tolerance merely to silence the error. Verify the selected
satellite/transmitter, base correction, transverter LO mapping, operator
no-Doppler tuning position, and the actual radio TX VFO first.

## CW receive CPU performance notes

This version caches ridge-scanner Hann windows and skips per-sample trigonometric de-chirping when the known Doppler rate is zero. The display waterfall remains approximately 20 Hz, while its 8192-point spectrum trace refreshes every fourth display frame. The single DeepCW ONNX Runtime session uses bounded CPU intra-op parallelism to reduce contention with the 120-ms tracker loop. Decode windows, hops, physical-evidence gates, tracker covariance and satellite TX interlocks remain unchanged.

For a meaningful before/after comparison, use the same PCM recording, lane count and display-cleanup mode, then compare total process CPU, completed/skipped inference windows and transcript accuracy. The Windows CPU percentages of an i5-10400 and an i7-14650HX are not directly comparable as per-inference cost metrics; real hardware benchmarks are still required.


### Planned real FFT (FFTW3f)

The CW receiver now uses a shared **forward real-to-complex (R2C)** FFT
abstraction for the Ridge Scanner (zero known Doppler rate), candidate detector,
model-rate DeepCW STFT, wideband shared DeepCW STFT, and live waterfall.
Rather than constructing a full complex spectrum of real AF PCM, the new
interface exposes only the unnormalised non-redundant bins `0..N/2`.
DeepCW window shape, spectral magnitude calibration, model input tensor
sizes and decoder thresholds remain unchanged. For nonzero known Doppler
de-chirping, the Ridge Scanner keeps its original double-precision complex
Fourier transform.

The preferred backend is **FFTW3f**, already packaged as
`libfftw3f-3.dll` by SkyRoof. FFTW owns SIMD-aligned scratch memory and the
execution plans are reused across decoder hops (rather than regenerated every
frame). FFTW's built-in hardware optimizations depend on the actual binary and
CPU features: this update does **not** imply AVX2/AVX-512 support is present in
every distributed FFTW build. If the native DLL or its R2C symbols cannot load,
the original Math.NET transform runs instead. The fallback preserves the
same unscaled forward convention.

This route follows the **measurement methodology**, not the code, of
[kfrlib/fft-benchmark](https://github.com/kfrlib/fft-benchmark).
The benchmark repository is MIT licensed; the FFTW library retains its own
GPL license. No KFR, MKL or IPP binary is added.

Every pull-request Compile Check performs a same-host, warmed, median-time
real-R2C versus previous complex-forward microbenchmark and uploads
`cw-fft-r2c-benchmark` (JSON Lines, 256/2048/3840/4096/8192/11520 samples).
This describes isolated per-transform compute cost, **not** total decoder CPU
or the i5-10400 performance of a different machine. Before judging the
change, compare recorded audio accuracy, worker skipped windows and total
process CPU using the same audio, receiver options and display settings.


### Incremental STFT cache and ONNX inference metrics

SkyRoof now memoizes Ridge Scanner zero-Doppler frame peaks by **absolute
input sample start** (not by a snapshot-relative frame number). The cache is
enabled only on the live append-only PCM frontend, and is cleared on a
timeline reset. Nonzero-rate Doppler de-chirp retains its uncached complex
FFT. This preserves the existing 80/15-ms and 240/120-ms STFT grids,
time-indexed ridge observations and measurement covariance.

The shared DeepCW wideband STFT has a bounded 1600-frame cache scoped to one
inference session. Only wholly **interior** STFT frames are eligible, because
the first/last frames use reflection relative to the current 6-second
window. A 1-second decode hop is not divisible by the 15-ms STFT hop, so
the absolute frame lattice cycles across three phases: reusing data by
relative row index would corrupt model input. SkyRoof reuses only an
*exactly matching absolute PCM interval*. Model metadata, feature scaling,
gates and inference tensor contents remain unchanged. Optional denoising
bypasses this cache.

The **Worker** status tooltip now reports Ridge/DeepCW STFT frame cache
hits/misses and mean ONNX Runtime `Run()` time. ONNX still reuses one CPU
InferenceSession with limited intra-op parallelism. Its contiguous output
tensor is decoded directly without allocating an extra logits copy; session
options are disposed after construction. Those are allocation/diagnostic
optimizations, **not** a claim of faster ONNX kernels or batched inference.
Compare the same PCM and RF settings on the same CPU using both the cache
counters and the completed/skipped decoder-window metrics.
