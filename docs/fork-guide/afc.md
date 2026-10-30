# Doppler-assisted FM / CW / SSB AFC

**Language:** English | [简体中文](../zh-cn/fork-guide/afc.md)

SkyRoof's AFC has an independent 12 kHz IF PCM capture path and shares the
existing radio Doppler model. **AFC only adds a transient RX correction**;
it does not edit transponder tuning, stored manual corrections, RIT, XIT,
or uplink/TX frequency. IC-9700 USB and LAN IF output are documented in
the [Icom IC-9700 manual](https://icomuk.co.uk/files/icom/PDF/productManual/IC-9700_ENG_Basic_6.pdf).

## Automatic RX CAT/IF polarity calibration (v1.60)

- With AFC Auto and RF/IF sign set to 0, the servo waits for a stable real 12-kHz IF reference and time-aligned RX CAT readbacks before a temporary RX-only calibration.
- One verified CAT probe/restore cycle is required: nominally 150 Hz for FM or 40 Hz for CW/SSB pilot, rounded to the configured CAT tuning step. Each edge requires an actual hardware readback and at least two settled IF observations. The return-to-original-frequency step must confirm the same sign before automatic AFC writes are permitted.
- A CAT request cannot acknowledge itself; the actual readback must follow the request within the same calibration phase. IF evidence is rejected until its averaging window has settled after CAT tuning. Failure text identifies the stage that timed out.
- The probe is an ephemeral RX layer, never a saved satellite correction, RIT/XIT or TX write. Lost reference, PTT, timeout, satellite/mode changes or manual tuning cancel/restore the probe. Failed calibration blocks automatic correction until reset/reacquisition.
- `Ignore Dial Knob` no longer suppresses the actual CAT RX frequency readings needed by AFC; it only suppresses dial-knob ownership feedback.
- The console now distinguishes actual CAT readback freshness and polarity-calibration state from signal lock. Signal lock alone does not imply closed-loop AFC readiness.

## Updated P1–P4 operator interface

The AFC console now has **Tracking** and **Advanced settings** tabs.
Tracking displays a measured 12-kHz IF spectrum, observed offset, error
from desired center, RX-only correction, confidence, frequency-direction
state, and CAT availability. Click the spectrum to select a CW/pilot
carrier; ordinary FM center tracking does not use a selected tone.

**Lock Here** preserves the current carrier's IF pitch.
**Center & Lock** attempts to move that selected carrier toward
`Desired center IF offset (Hz)`. This is **independent** of
`Target search IF offset (Hz)`, which selects the original signal
to search for. Do not use the same setting for both roles.

Changes to ordinary tracking parameters do not reopen WASAPI. Switching
devices, IF transport or reference frequency intentionally restarts the
capture source. Satellite/transponder changes and IF target selection
invalidate the old PCM generation and require reacquisition.

CAT readbacks are timestamped on the CAT worker thread, and only
readbacks close to the IF sample time can contribute to frequency-axis
polarity learning. A requested CAT write is **not** treated as a
confirmed hardware readback. Confirmed, repeatedly diverging AFC
corrections cause the controller to enter Fault and stop auto writes.

## RS-BA1 color mapping and IF reference level

The IF scope uses SkyRoof's existing Icom LAN Scope **161-step Classic
palette**, with a navy instrument background, green live trace and yellow
reference/peak markers. The waterfall uses blue, cyan, green, yellow and
white to represent progressively stronger signal levels. The plotting
surface is navy in all supported SkyRoof Light, Dark, GitHub Light/Dark
and System themes; surrounding dialog controls follow the app theme.

Under Advanced settings, **IF 频谱 REF LEVEL (dB)** defaults to **+15 dB**
and persists in the AFC settings. Increasing it reduces the displayed
intensity; decreasing it emphasizes weaker signals. It adjusts display
headroom and waterfall color levels only, **not** the audio samples, IF
frequency estimates, AFC confidence, CAT readback, or RX control.

Vertical dB labels are display levels, **not hardware-calibrated dBm**.
The scope heading reads “12 kHz IF · REF +15 dB · 频谱”.

## Getting started

1. On IC-9700 choose `SET > Connectors > USB AF/IF Output > Output Select > IF`
   for the USB capture device. On compatible IC-705 use the documented USB
   IF route. The physical output is a **real 12 kHz IF audio signal, not
   12 kS/s PCM and not complex I/Q**.
2. Right-click the **downlink frequency label**, select **FM / CW / SSB
   AFC...**, or double-click the RX frequency. Set the Windows **IF input**
   device in the dialog and press **Start IF**. Alternatively select
   **IF transport = PlaybackLoopback** and the render endpoint carrying
   IC-9700 LAN / IC-705 remote IF audio.
3. Choose **Assist** to observe estimated IF frequency without CAT changes.
   Choose **Auto** to compensate RX only. If the mapping sign cannot yet be
   learned automatically, the controller remains in observation mode. This
   does not require a calibration prompt on every launch.
4. For CW, click the carrier in the live spectrum or enter its original
   offset in **Target search IF offset (Hz)** on Advanced settings.
   **Lock Here** preserves the carrier's current IF pitch;
   **Center & Lock** moves it toward the independent
   **Desired center IF offset (Hz)** setting.
5. In SSB, normal suppressed-carrier speech has no recoverable absolute
   frequency reference. Only enable **SSB reference available** if an actual
   stable pilot/CW beacon is present. Without a reference, only Assist is
   provided.

## Important signal / CAT details

- The CI-V 27 00 spectrum is **not** used for AFC: the signal input is
  Windows WASAPI PCM carrying the radio's selected IF output.
- The default input is 48 kS/s real PCM; a complex baseband is constructed
  using digital mixing, FIR filtering and decimation before estimation.
- Existing CW Console audio is **AF**. If the radio's USB output is switched
  to IF, choose a different AF feed, such as an appropriately routed
  RS-BA1 playback loopback, for CW decoding.
- IC-9700 manuals specify a 12 kHz IF output but do not document one
  universal RF/IF tuning polarity for every combination of mode/filter.
  `RF/IF direction = 0` attempts automatic inference by comparing IF
  motion with CAT readback while a signal is tracked. `+1` or `-1` is
  an explicit fixed override for known equipment.
- PTT freezes the AFC correction updates. Target identity/source-generation
  changes clear old correction. A CAT TX frequency is never modified by AFC.
- Avoid running multiple active CAT controllers that can fight over the RX
  dial. The existing SkyRoof/SkyCAT control authority remains responsible
  for the actual CI-V write.
- The IF sample clock and radio CAT readback are not hardware-synchronized.
  Treat the AFC estimate as a slow servo and do not use it as a high-rate
  PLL controlling every IF sample.

## Signal-specific methods

| Mode | Estimator | Meaning of Auto |
|---|---|---|
| FM | low-rate robust discriminator, time windows | Correct only persistent center-frequency residual; intermittent speech holds |
| CW | windowed narrowband FFT peak interpolation | Follow user-selected IF tone; no switching to strongest signal on dropout |
| SSB | explicit pilot/reference narrowband estimate | Auto only if an actual reference exists; ordinary speech is Assist |

## Validation and limitations

This feature includes synthetic xUnit tests for 12 kHz IF tone, missing SSB
reference, unsigned polarity, dropouts, PTT and session invalidation.
**These tests are not hardware certification**. In particular, before
claiming measured performance for a given IC-9700/IC-705 firmware/IF
routing, record known-tone IF PCM and corresponding RX CAT readback in
FM/CW/USB/LSB, and check the direction in both tuning directions.

References: [STRF](https://github.com/cbassa/strf),
[SDRangel Frequency Tracker](https://github.com/f4exb/sdrangel/blob/master/plugins/channelrx/freqtracker/readme.md),
[Rife & Boorstyn (1974)](https://doi.org/10.1109/TIT.1974.1055282),
[Kay (1989)](https://doi.org/10.1109/29.45547),
[Boashash (1992)](https://doi.org/10.1109/5.135378).
