# CW Console

The CW Console is SkyRoof's receive-and-keyer panel for simultaneous CW signals.
It combines multi-carrier tracking, DeepCW text decoding, a live AF waterfall,
continuous per-lane transcripts, and an explicitly armed IC-9700 Command-17
text keyer.

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

- **SDR** — reuses SkyRoof's existing 48 kHz Slicer audio.
- **WASAPI Capture** — opens the selected Windows capture endpoint.
- **RS-BA1 Loopback** — captures a selected Windows render endpoint in loopback
  mode, downmixes it to mono, and resamples it to 48 kHz.

RS-BA1 loopback captures the **whole render-endpoint mix**, not only the
Remote Utility process. A dedicated playback endpoint is recommended when you
want only RS-BA1 receiver audio.

Changing the source or changing the actual audio-device ID starts a new CW
timeline. Tracks and transcript state from the previous device are discarded so
two sources cannot be mixed into one lane history.

CW receive is disabled by default. Use **Start** in the panel after selecting
the intended source.

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

The lane table and waterfall prefer a stable fixed-lag association ID:

- **H123** — stable AssociationHint identity.
- **T17** — transient TrackId fallback when no association hint exists yet.

A lane can therefore keep the same visible identity through a frequency
crossing even if the low-level tracker instance changes.

### Waterfall overlays

The CW waterfall follows the current scanner AF range. Track overlays show:

- Active / Hold / Ambiguous state;
- the selected lane;
- a translucent **±2 sigma** frequency-uncertainty band from the Kalman state.

The uncertainty band is diagnostic. A wider band means the tracker is less
certain about the instantaneous AF position; it is not an RF filter width.

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

CW transmit has two independent gates:

1. **Settings > CW Console > Enable CW Transmit** must be enabled.
2. The operator must press **Arm TX** in the CW Console.

**Arm state is never persisted.** Every new application/panel session starts
disarmed.

SkyRoof does not automatically:

- select CW or CW-R;
- enable Semi/Full BK-IN;
- assert PTT for this keyer path;
- choose a transmit frequency from a decoded receive lane.

Before arming, configure the radio yourself for CW/CW-R and Semi/Full BK-IN.

## SkyCAT keyer connection

The text keyer uses SkyCAT's dedicated loopback-only Command-17 endpoint,
normally:

`127.0.0.1:4538`

SkyCAT owns the hardware lease. It verifies the radio state and serializes the
keyer against other transmit users. A disconnect is also a fail-safe: SkyCAT
issues Command 17 `FF` STOP when the keyer client disappears.

The CW Console displays the actual key-speed-derived WPM and the watchdog
countdown while a message is active.

## Satellite transmit interlock

Satellite CW sends add another safety layer.

When TX is armed, SkyRoof captures the selected satellite/transmitter,
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

Press **Send** only after TX is armed. SkyRoof performs the same radio
preflight immediately before every message. The message lease remains open
until STOP or watchdog completion.

The watchdog estimates Morse duration from the IC-9700 KEYRAW key-speed value,
adds a safety margin, and sends STOP if the message does not finish within the
bounded interval.

The red **STOP** button is independent of message composition and should be
used whenever the transmit state is uncertain.

## F1-F8 message macros

Configure presets under:

**Settings > CW Console > CW Message Macros**

The defaults are empty.

- **F1 ... F8** or clicking a macro button: load the preset into the composer
  only.
- **Shift+F1 ... Shift+F8**: explicitly send that preset through the complete
  TX safety state machine.
- Ctrl/Alt-modified function keys are not captured as CW transmit shortcuts.

Keyboard auto-repeat cannot queue multiple macro sends while the first
asynchronous TX preflight is still running.

Macros do not bypass Enable TX, Arm, CW/BK-IN checks, the satellite TXHZ/SENDHZ
guard, the lease, watchdog, STOP, or disconnect fail-safe.

There is no macro queue and no auto-reply.

## HamNoise status

HamNoise Classic and CW V2 are currently **benchmark-only research backends**.
They are not shown in the CW Console and are not included in production
installers.

The production decoder continues to use the raw wideband activity-aware
multi-track separation path.

## First RF test

Perform the first transmit test on a dummy load or otherwise controlled,
low-power setup.

Recommended sequence:

1. disable/verify RF as appropriate before changing settings;
2. set the IC-9700 TX VFO to CW or CW-R yourself;
3. enable Semi/Full BK-IN yourself;
4. verify the expected satellite/transverter TX VFO shown in the Console;
5. enable **CW Transmit** in Settings;
6. Arm TX;
7. send a very short test message;
8. press STOP and verify immediate keying termination;
9. verify Doppler catch-up after the lease releases;
10. only then proceed to an on-air test that is permitted by your licence and
    local band plan.

## Recorded-audio regression benchmark

SkyRoof also includes an offline recorded-WAV corpus runner for regression
testing the whole receive chain. See
[Recorded CW corpus](../../benchmarks/cw-recorded-corpus/README.md).

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

### Arm fails

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
