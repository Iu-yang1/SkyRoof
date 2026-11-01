# IF-AF-Test: 12-kHz IF to SSB/CW/FM audio for WSJT-X

Experimental branch `IF-AF-Test` only; disabled by default, no radio PTT/CAT changes and not part of an official release.

SkyRoof now provides a shared, single-owner 48-kS/s real 12-kHz IF capture and an independent mono AF → VAC path. A quarter-rate NCO translates the real IF; a 257-tap complex FIR selects USB (positive) or LSB (negative) frequencies in a nominal 180–3300 Hz passband, rather than folding both sidebands with a real product detector. CW selects a narrower 250–1150-Hz band. FM uses a 129-tap complex lowpass, conjugate-product discriminator, DC blocker and 95-tap AF lowpass. FM deviations 2.5 and 5 kHz are supported in configuration. Output is a separate VAC soundcard, *not* the SDR Slicer Output Stream.

Settings → **12 kHz IF -> AF / WSJT-X (Test)**: select IC-9700's USB microphone/capture endpoint carrying IF, normally the Left channel; choose a dedicated virtual playback endpoint (e.g. VB-CABLE **CABLE Input**, never radio USB Audio CODEC speaker/TX); enable the feature; choose FollowRadio or explicit USB/LSB/CW/FM. WSJT-X receives from corresponding **CABLE Output**. The SkyCAT 4534 proxy prevents conflicting frequency/mode writers while SkyRoof owns satellite Doppler.

When source device matches, AFC and SSTV can reuse the same capture samples; the CW module still requires its own AF input and is not intrinsically a shared raw-IF client. The old SkyRoof SDR Audio to VAC route must not target the same endpoint. The VAC status tooltip reports RMS, sample count, drops, AF clipping and queue age; it does not report end-to-end audio latency.

Automated tests synthesize 48k real 12k IF (sidebands, AWGN, opposite-sideband blocker, 16-bit quantization, CW, FM, frame partitions and stereo channel cancellation), not real IC-9700 digitizer measurements. No hardware ENOB/NF/sensitivity or synchronized WSJT-X decode-rate equivalence is claimed.

Architecture references: [GNU Radio frequency-translating FIR filter](https://wiki.gnuradio.org/index.php/Frequency_Xlating_FIR_Filter), [GNU Radio source](https://github.com/gnuradio/gnuradio/blob/main/gr-filter/lib/freq_xlating_fir_filter_impl.cc), [SoapySDR streaming format/ownership](https://pothosware.github.io/SoapySDR/doxygen/latest/classSoapySDR_1_1Device.html). No source code was copied.

## Added: simultaneous IC-9700 USB IF vs original LAN AF recording

Open **Recorder → Record dropdown → Record IC-9700 USB IF + LAN AF…**, or **Tools → Record IC-9700 IF + LAN AF (Test)…**. Set IC-9700 `USB AF/IF Output=IF` and `LAN AF/IF Output=AF`, route RS-BA1 Remote Utility LAN AF to a dedicated Windows playback/render endpoint, and open the FM/SSB squelch. Select the USB IF **capture** endpoint, LAN AF **render** endpoint, and IF input Left/Right/Mix channel; choose output folder and record 60–120 seconds of the same RF signal. Press **Stop and Save**.

The session directory under `IfAfComparisons` includes two independent 48-kHz mono 32-bit float WAV files (`ic9700_usb_12khz_if.wav`, `ic9700_lan_original_af.wav`), corresponding per-block `.csv` files containing sample offsets/counts and callback UTC, and `capture.json` with paths, endpoint identities, selected IF channel, start/stop UTC, count/drops/errors, receiver mode/frequency and optional operator notes. Upload **all five files together** for clock alignment, comparison and DSP benchmarking.

Recording is independent of SkyRoof's separate `Recorder` WAV utility. If experimental IF-AF is already running on the same USB capture device **and input channel**, the recorder subscribes to the shared *unmodified IF PCM* instead of opening another WASAPI stream. Otherwise it captures the IF independently. LAN AF always comes from its own loopback of the chosen RS-BA1 render endpoint, *not* from the SkyRoof software-demodulated VAC output.

**Caveats:** callback timestamps originate in Windows rather than sample-clock hardware; the two captures are not necessarily sample-synchronous. Both WAVs are application-level mono PCM after possible WASAPI mixing/resampling, not raw USB packets. Windows loopback captures all mixed playback on its selected endpoint, and may omit silent intervals. Review CSV gap patterns and JSON drop counters; do not claim calibrated IC-9700 sensitivity or an exact hardware timebase.

## Added: fast, recoverable AFC for transient satellite FM/CW/SSB

The experimental RX-only residual AFC now uses mode-specific IF windows and independent source-confidence gates: FM minimum 400 ms, 500-ms analysis window and 125-ms cadence; CW minimum 375 ms, 550-ms window, 125-ms cadence and adaptive 4096/8192-bin local tone detection; SSB with explicit stable pilot minimum 375 ms, 600-ms window and 150-ms cadence; SSB without pilot remains **relative drift only (Assist)** with a 750-ms minimum.

A bounded alpha-beta residual predictor coasts through 1.5 s FM / 2.5 s CW / 2.0 s piloted-SSB gaps. Predicted residual never alone authorizes new CAT AFC steps; real, unambiguous carrier measurements do. Once the coast horizon expires, prior AFC-only RX correction decays at 160 Hz/s back toward the orbital Doppler model; it is frozen during PTT. No changes are made to TX, persisted transponder corrections or the orbital model.

Verified, time-aligned **AFC-authored** CAT readbacks indicating repeated divergence cause bounded rollback and reacquisition, rather than latching Fault while leaving a stale offset. Sign reversal is eligible only for auto polarity with repeated independent CAT/IF evidence. An explicit manual sign is never silently flipped. Rate limits and AFC servo bounds remain in place.

`AFC trace` and `AFC watchdog` logs expose IF candidate, quality and sigma, estimated/observed residual, CAT readback attribution, correction, state transitions and PTT status. Synthetic tests include transient 400-ms FM, keyed CW, piloted SSB, drop-outs, outliers and divergence. These are **software checks**, not measured IC-9700 AFC lock-time or RF sensitivity guarantees.
