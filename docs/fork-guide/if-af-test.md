# IF-AF-Test: 12-kHz IF to SSB/CW/FM audio for WSJT-X

Experimental branch `IF-AF-Test` only; disabled by default, with no PTT/CAT writes from the IF-to-AF demodulator. The separately enabled AFC Auto controller can write bounded **RX-only** CAT frequency corrections; this experiment has not been merged into an official release.

SkyRoof now provides a shared, single-owner 48-kS/s real 12-kHz IF capture and an independent mono AF → VAC path. A quarter-rate NCO translates the real IF; a 257-tap complex FIR selects USB (positive) or LSB (negative) frequencies in one of seven nominal SSB audio passbands (1.2/1.8/2.1/2.4/2.7/3.0/3.3 kHz, 180 Hz lower edge), rather than folding both sidebands with a real product detector. CW selects a 700-Hz-centered audio filter with 100/200/300/400/500/800/1000-Hz selectable bandwidth. FM uses a 129-tap complex lowpass, conjugate-product discriminator, DC blocker and 95-tap AF lowpass. The UI selects a nominal 6.25/12-kHz full IF bandwidth preset, with associated AF cutoff and discriminator-deviation normalization. Output is a separate VAC soundcard, *not* the SDR Slicer Output Stream.

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

A bounded alpha-beta residual predictor coasts through 1.5 s FM / 2.5 s CW / 2.0 s piloted-SSB gaps. Predicted residual never alone authorizes new CAT AFC steps; real, unambiguous carrier measurements do. Once the coast horizon expires, the default `HoldLast` policy retains the last verified AFC RX correction while orbital Doppler continues. Only an explicitly selected `GentleRelease` releases at 10 Hz/s after 30 seconds without signal; release is frozen during PTT. No changes are made to TX, persisted transponder corrections or the orbital model.

Verified, time-aligned **AFC-authored** CAT readbacks indicating repeated divergence cause bounded rollback and reacquisition, rather than latching Fault while leaving a stale offset. Sign reversal is eligible only for auto polarity with repeated independent CAT/IF evidence. An explicit manual sign is never silently flipped. Rate limits and AFC servo bounds remain in place.

`AFC trace` and `AFC watchdog` logs expose IF candidate, quality and sigma, estimated/observed residual, CAT readback attribution, correction, state transitions and PTT status. Synthetic tests include transient 400-ms FM, keyed CW, piloted SSB, drop-outs, outliers and divergence. These are **software checks**, not measured IC-9700 AFC lock-time or RF sensitivity guarantees.

## Selectable AFC acquisition strategy

Under **AFC → Advanced settings → Acquisition strategy (speed / sensitivity)** select **Adaptive (default)**, **Fast**, **Balanced**, or **Sensitive**. Editing changes the acquisition profile immediately, invalidates stale IF estimates, and does not reopen WASAPI.

| Preset | First FM IF window | CW | Piloted SSB | Behavior |
| --- | ---: | ---: | ---: | --- |
| Adaptive | 250 ms | 250 ms | 250 ms | Quick first estimate, grow the window, adaptive 4096/8192/16384-point FFT and additional FM overlapping-window check |
| Fast | 250 ms | 250 ms | 300 ms | Lower CPU load and short transmissions |
| Balanced | 400 ms | 375 ms | 375 ms | Previous reference behavior |
| Sensitive | 500 ms | 375 ms | 375 ms | More integration for weak conditions |

These are **minimum PCM observation durations**, not measured lock latency, guaranteed sensitivity improvements, or CAT tuning rates. The absolute carrier confidence threshold and readback safeguards remain unchanged. Overlapping windows share samples and are not counted as independent confidence confirmations. Suppressed-carrier SSB without a pilot remains Assist-only. A single in-flight DSP measurement prevents CPU overload; switch to Fast if CPU is limited.

## IF-AF-Audio device architecture

For the signed virtual microphone implementation and SkyRoof link-bar details, see [IF-AF-Audio virtual microphone and low-latency pipeline](../../drivers/IF-AF-Audio/README.md). The in-app user-mode route is implemented in this test branch; the signed WDK driver is not yet built or distributed.


## SO-50-style FM acquisition and signal-loss policy

Original code required >=40% quality at the FM estimator and >=60% at the AFC controller/predictor. Short low-quality but narrow-sigma FM candidates were ignored. The test branch now permits FM candidates >=22% quality and sufficient relative spectral evidence, but CAT remains gated behind **three consistent new-time epochs separated by >=80 ms**, candidate sigma <=45 Hz, and successive offset disagreement <=max(55 Hz, 2.5*sigma). These overlapped analysis windows are NOT statistically independent. CW and SSB quality thresholds are unaffected.

**Settings → AFC → SignalLossPolicy**:

- `HoldLast` (default): maintain the last *verified* RX AFC residual through FM voice gaps, QSB and CW key-up while orbital Doppler continues as usual. Coasting can expire into Hold/Lost *without changing the retained frequency correction*.
- `GentleRelease`: an explicitly selected alternative; wait 30 seconds after the last valid signal, then release at 10 Hz/s, frozen during PTT.
- Manual retune, transponder/mode change, explicit AFC disable/reinitialization and independently verified wrong CAT polarity still have their own appropriate reset/rollback logic.

The UI distinguishes `Weak FM temporally verified` from raw single-window 26% quality rather than falsely displaying it as 60%. These are software acceptance gates, not an IC-9700 lock-time guarantee.

## Vendor virtual cable selection (current IF-AF-Test)

We have **stopped distributing or automatically building the experimental
SysVAD kernel driver** in the SkyRoof installer. Instead, IF-AF uses
officially installed vendor drivers via Windows WASAPI, no custom driver
or special test-signing boot policy required.

- **Recommended: [VB-CABLE](https://vb-audio.com/Cable/)** from VB-Audio.
  SkyRoof sends 48-kHz mono float AF to the Windows **CABLE Input**
  *playback* device; WSJT-X must open **CABLE Output** as a *recording*
  device. The Windows shared-mode audio engine may convert the format to
  match the installed cable.
- **Alternative: [Virtual Audio Cable Lite](https://vac.muzychenko.net/en/)**
  by Eugene Muzychenko, which normally exposes Line 1 on both playback
  and recording sides. Observe the Lite version's non-commercial distribution
  restrictions and obtain the original package from the vendor.
- **AudioRoute = AutoSignedCable** (default for new settings) detects a
  single unambiguous pair of active vendor Windows audio endpoints.
  **ExistingVirtualCable** remains the manual fallback for renamed cables,
  third-party drivers or explicitly chosen dedicated output endpoints.
  Existing serialized routes retain their original numeric values.
- If a complete pair is absent or ambiguous, automatic routing fails closed;
  it NEVER silently selects the system default render device or Icom USB
  TX audio. The installed driver is governed by Windows code integrity;
  name recognition alone is **not** cryptographic verification of a signer.
- Settings provides **Install VB-CABLE**, **Uninstall VB-CABLE**, and endpoint rescan. The experimental installer embeds the **unaltered original base VB-CABLE ZIP** (including the vendor's own INF/SYS/CAT), verifies its SHA-256 and signed artifacts, and invokes vendor setup with UAC permission. Removal requires user confirmation and SkyRoof-owned installation; never remove another application's existing driver.
- Do not use the *same* cable as an independent Slicer → VAC output
  while IF-AF is running. The user-supplied USB IF and LAN original AF
  recordings are not a third-party cable round-trip validation.

The original `drivers/IF-AF-Audio/` experiment is preserved for research,
but its WDK Actions and separate unsigned-driver packaging workflow are
disabled. The experimental installer now bundles SkyRoof plus the **unmodified official basic VB-CABLE ZIP**, with an opt-in automated installation of the vendor driver. CI smoke tests skip driver installation.


**2026-11-01 packaging update:** Under the [official VB-Audio donationware bundling grant](https://vb-audio.com/Services/licensing.htm), SkyRoof embeds the unmodified official base `VBCABLE_Driver_Pack45.zip`, verifies the package SHA-256 and executable/catalog signatures, and invokes VB-Audio's own driver setup with administrator consent. The default interactive installer offers optional automated installation, requires a Windows restart, and never changes Secure Boot or test-signing policy. A shared vendor driver installed by somebody else is not removed by SkyRoof. This does not apply to paid A+B/C+D drivers.

## 2026-11-01 acceptance notes

- FollowRadio uses SkyRoof's current terrestrial RX selection or satellite downlink mode; do not assume a manual external CI-V mode change will be followed automatically. CW-LSB is manually selectable. AFC independent WASAPI IF capture defaults to Left, configurable separately; IF-AF/AFC share unmodified IF only when both device ID and selected channel match.
- Legacy SDR Audio-to-VAC and automatically selected VB-CABLE CABLE Input may not be used simultaneously. The IF-AF path also rejects using the same VB-CABLE recording endpoint (CABLE Output) as its IF source. Detect missing 10-ms PCM blocks using the capture sample sequence; do not splice NCO phase across a lost block.
- In a 30-second segment of actual 2026-11-01 IC-9700 **USB** IF and LAN AF captures, a matching 257-tap complex USB sideband filter produced an approximately **0.953** short-time envelope correlation after +320-ms relative alignment; the wrong LSB sideband produced approximately **0.018**. Other modes and real VB-CABLE/WSJT-X receive performance still require independent hardware tests.
- For real hardware acceptance, verify the full VB-CABLE render/capture pair and reboot, start with AFC Assist, test USB/LSB/CW/FM separately, inspect IF PCM counters and output underruns, confirm CW pitch and FM deviation, then carefully enable AFC Auto with verified CAT frequency direction. Ensure PTT freezes AFC writes and HoldLast retains the RX correction through signal gaps. Compare WSJT-X FT4/FT8/Q65 decodes over multiple periods, not one isolated decode.

## VB-CABLE installation failures (exit code 1)

An exit code of 1 from the driver manager is a generic PowerShell failure, not proof of a driver-signature issue. Use a new experimental integrated installer, then retry **Settings → Install VB-CABLE**. The elevated manager now writes `ThirdParty\\VB-CABLE\\VB-CABLE-manager.log` under the SkyRoof installation folder, and the failure dialog includes its precise `VB-CABLE FAILED at stage [...]` message, the vendor setup exit status when available, and the log path.

- `Bundled archive and SHA256`: repair the complete original archive and hash manifest by reinstalling the bundled SkyRoof installer.
- `Original setup Authenticode signature` / `Original driver catalog signature`: inspect Windows certificate validation; never bypass code-signing policy.
- `Original vendor setup (Install)`: inspect the log and `C:\\Windows\\INF\\setupapi.dev.log` for the Windows driver-installation reason.
- `Windows device discovery`: inspect `Get-PnpDevice -Class MEDIA` and `Get-PnpDevice -Class AudioEndpoint` output.
- `Partial VB-CABLE installation detected`: reboot first; if still incomplete, repair through the original vendor setup/Device Manager, not by deleting independently owned drivers.

After a successful install, reboot and confirm the **CABLE Input** render and **CABLE Output** capture endpoints. CI validates payload integrity and script diagnostics, but does not claim a real kernel-driver installation test.

## 2026-11-01: IF→AF route cleanup and native status LEDs

The AF audio route dropdown now contains only `AutoSignedCable` (automatic paired VB-CABLE/VAC Lite render and capture endpoints) and `ExistingVirtualCable` (manual render endpoint; capture pairing not verified). Persisted numeric value `0` or the historic name `PreferIfAfAudio` migrates to `AutoSignedCable`; the old numeric values `1` and `2` retain their meaning.

The footer now uses the same native ToolStrip Webdings LED and Segoe UI text style as RX CAT for `IF`, `DDC`, `AF`, and `V-MIC`. Grey means inactive; green means the local stage is working; amber means warming up, recovery, a recent drop/underrun or an unverified capture pairing; red means a stopped/failed stage. A green V-MIC does **not** prove WSJT-X receives or decodes. Manual cables deliberately show an amber V-MIC because the capture endpoint is not independently paired. Click any LED, label or Q/P readout to open existing diagnostics. Q is estimated software queue age; P is requested WASAPI period, not measured end-to-end latency.

## 2026-11-01: historical clipping vs live overload

`Clipped since start` is a **cumulative** count of AF samples limited to ±0.999. Previously the footer used `ClippedSamples > 0`, permanently latching AF/V-MIC amber after even one old clip. The new LEDs compare successive status snapshots; they remain amber for about two seconds after newly clipped samples, then return green if all stages are healthy. No historical counts are erased.

The diagnostic popup now distinguishes cumulative clips, new clips during the latest ~0.5 s UI interval, clips in the latest ~10 ms processing block, latest-block AF RMS and **pre-limit** AF peak. An input RMS measured in the latest block cannot establish the signal level during earlier accumulated clips. If new clipping persists or pre-limit peak reaches 0 dBFS, inspect IF capture, host audio controls, mode, AF gain and the recorded CABLE Output signal. Temporarily reducing AF gain by 6–12 dB can help diagnose headroom, but does not replace fixing the underlying cause.

## Mode-aware post-demodulation AF leveling: SSB/CW slow AGC and independent FM

The 48-kHz 12-kHz IF DDC and USB/LSB/CW/FM demodulator stay unchanged. Only **post-demodulation AF** is leveled; raw IF subscribers (AFC and SSTV) are unaffected.

- USB/LSB/CW (including digital sidebands) use bounded slow RMS AGC by default: −24 dBFS target, +24 dB maximum adaptive boost; fast downward/slow upward gain tracking; silence below −80 dBFS freezes the level. SSB and CW have separate manual post-AGC trims and a switch to disable AGC for amplitude-linear measurements.
- FM retains the existing quadrature discriminator with its calibrated deviation setting and an independent **FM output gain**, default −15 dB. FM deviation normalization must not be confused with SSB IF amplitude.
- A per-10-ms-block peak ceiling at 0.89 (about −1 dBFS) reduces whole-block gain without hard waveform clipping. This is a no-lookahead block-level safety control, not a true-peak limiter and not proof of distortion-free RF demodulation. The diagnostics expose pre-ceiling peak, final applied gain, limiter reduction, and count of peak-protected blocks.
- Previous `GainDb` values migrate to FM gain only: −15 dB or quieter preserved; louder historic presets clamped to the safe −15 dB baseline. SSB/CW gain controls are independent.

Evaluate with actual 60–120 s IF and CABLE Output audio recordings for FM and SSB/CW, and compare WSJT-X FT4/FT8 decode stability. Passing synthetic CI tests cannot substitute for a radio/VB-CABLE hardware test.

## Simple mode bandwidth selectors

Settings → Receive filters contains only three user-facing selectors; there are no raw IF/AF cutoff fields to calculate. SSB offers **1.2 / 1.8 / 2.1 / 2.4 / 2.7 / 3.0 / 3.3 kHz** (default 3.0 kHz; 180 Hz low edge). CW offers **100 / 200 / 300 / 400 / 500 / 800 / 1000 Hz** (default 500 Hz, 700 Hz center AF pitch). FM offers nominal **6.25 / 12 kHz** full complex IF bandwidth (default 12 kHz), internally controlling IF lowpass, AF lowpass and deviation-normalization values. Finite FIR transition skirts mean these are **not** measured brick-wall/-3 dB widths or radio channel spacings.

Changes take effect on Apply via the existing IF→AF pipeline recreation, not RF CAT/PTT or the AFC raw-IF path. For FT4/FT8 use wide SSB and verify edge tones. For 100/200 Hz CW ensure the receiver sidetone is close to 700 Hz. For ordinary narrowband FM start with the 12-kHz preset, and use 6.25-kHz only for genuinely narrower signals.

## P0–P3 receiver DSP increments (2026-11-02)

**P0 — calibrated bandwidths.** The 257-tap Blackman complex SSB FIR and the FM IF/AF lowpass filters now solve for the design sinc cutoff whose frequency response reaches −3 dB at the intended edge, rather than incorrectly labeling the middle of the transition band as the useful cutoff. The two CW IIR biquads use an approximately 1.56× design-bandwidth compensation with a broader complex sideband preselector. The old serialized FM enum value `Normal12k=12000` remains supported but its UI label is now *12.5 kHz channel spacing*. The 6.25/12.5-kHz presets select 6/11-kHz nominal IF channel bandwidth, 2.1/3-kHz AF cutoff and ±0.9/2.5-kHz assumed deviation, inspired by SDRangel. Finite FIR transition skirts still require measurement.

**P1 — voice vs digital.** When FollowRadio sees `USB_D` / `LSB_D`, the IF-AF pipeline selects its internal `UsbDigital` / `LsbDigital` mode and passes a wider 100–3500-Hz audio selection filter without the voice RMS AGC or optional voice notch. Analog SSB/CW retains bounded slow RMS AGC. Optional FM voice squelch is RF-power envelope gating (~50ms smoothing and ~10ms fade), not proof of a real signal. Optional FM 50/75-μs deemphasis is *off* by default, as are optional fixed 1/1.5-kHz SSB speech notches and the experimental AutoTracking stable-tone notch (three 171-ms 12-kHz Goertzel windows, >=20 dB spectral prominence, soft 40-ms ramp). Do not enable voice processing for FM telemetry or data without validation.

**P2 — reduced DSP cost.** The 12-kHz IF and 48-kHz sampling rate enable exact 4-phase real convolution weights for USB/LSB/CW, replacing per-tap complex multiplications without changing stream phase. FM keeps its own complex FIR/discriminator. Temporary AF buffers use `ArrayPool<float>`, returned after synchronous WASAPI ring writes. Peak ceiling reduction engages immediately and releases over ~150ms to reduce abrupt block-boundary gain changes; DSP block execution time is exposed in diagnostics. **No full multirate decimation/resampling redesign has been deployed**; that requires separate real-time profiling and resampler-equivalence testing.

**P3 — optional voice enhancement.** Implemented deterministic fixed notches (1/1.5 kHz), an experimental optional AutoTracking stable-tone notch for analog SSB speech, 50/75-μs FM deemphasis, and optional RF-power FM squelch, all default-disabled. AutoTracking can mistake long tonal speech for an interferer; disable it if speech quality degrades. An experimental, opt-in **SSB voice noise reduction = Mild** has been added using 2048-point STFT, 50% overlap, sqrt-Hann WOLA and median-spectrum noise-floor masking smoothed across frames (up to ~8 dB bin attenuation). It adds ~42.7 ms processing delay and applies ONLY to analog SSB speech; USB-D/LSB-D/CW/FM bypass it. It is not validated MMSE-LSA or neural denoising, and its musical-noise/voice-distortion tradeoffs require hardware WAV comparisons. Stronger neural/broadband denoising is not implemented.

Reference: [GNU Radio firdes](https://www.gnuradio.org/doc/doxygen/classgr_1_1filter_1_1firdes.html), [SDRangel NFM](https://github.com/f4exb/sdrangel/blob/master/plugins/channelrx/demodnfm/readme.md), [GNU Radio FM deemphasis](https://wiki.gnuradio.org/index.php/FM_Deemphasis), [Power Squelch](https://wiki.gnuradio.org/index.php/Power_Squelch). CI synthetic audio/filter tests are not hardware SNR/SINAD/QSO validation; compare recorded IF, LAN AF and VB-CABLE output in real IC-9700 runs.

### Optional voice noise reduction: test notes

Compare Off vs Mild against the same saved 60–120-second IF recording while holding receive filter, AGC, gain and CABLE endpoint fixed. Measure speech intelligibility, spectral floor, retained speech frequencies, peak limiting and the extra ~42.7 ms STFT latency. Avoid using Mild for FT4/FT8/Q65; the dedicated USB-D/LSB-D path bypasses it regardless. AutoTracking notch may suppress long-lived speech harmonics mistaken for a carrier; keep it off if speech quality worsens.
