# IF-AF-Test: 12-kHz IF to SSB/CW/FM audio for WSJT-X

Experimental branch `IF-AF-Test` only; disabled by default, no radio PTT/CAT changes and not part of an official release.

SkyRoof now provides a shared, single-owner 48-kS/s real 12-kHz IF capture and an independent mono AF → VAC path. A quarter-rate NCO translates the real IF; a 257-tap complex FIR selects USB (positive) or LSB (negative) frequencies in a nominal 180–3300 Hz passband, rather than folding both sidebands with a real product detector. CW selects a narrower 250–1150-Hz band. FM uses a 129-tap complex lowpass, conjugate-product discriminator, DC blocker and 95-tap AF lowpass. FM deviations 2.5 and 5 kHz are supported in configuration. Output is a separate VAC soundcard, *not* the SDR Slicer Output Stream.

Settings → **12 kHz IF -> AF / WSJT-X (Test)**: select IC-9700's USB microphone/capture endpoint carrying IF, normally the Left channel; choose a dedicated virtual playback endpoint (e.g. VB-CABLE **CABLE Input**, never radio USB Audio CODEC speaker/TX); enable the feature; choose FollowRadio or explicit USB/LSB/CW/FM. WSJT-X receives from corresponding **CABLE Output**. The SkyCAT 4534 proxy prevents conflicting frequency/mode writers while SkyRoof owns satellite Doppler.

When source device matches, AFC and SSTV can reuse the same capture samples; the CW module still requires its own AF input and is not intrinsically a shared raw-IF client. The old SkyRoof SDR Audio to VAC route must not target the same endpoint. The VAC status tooltip reports RMS, sample count, drops, AF clipping and queue age; it does not report end-to-end audio latency.

Automated tests synthesize 48k real 12k IF (sidebands, AWGN, opposite-sideband blocker, 16-bit quantization, CW, FM, frame partitions and stereo channel cancellation), not real IC-9700 digitizer measurements. No hardware ENOB/NF/sensitivity or synchronized WSJT-X decode-rate equivalence is claimed.

Architecture references: [GNU Radio frequency-translating FIR filter](https://wiki.gnuradio.org/index.php/Frequency_Xlating_FIR_Filter), [GNU Radio source](https://github.com/gnuradio/gnuradio/blob/main/gr-filter/lib/freq_xlating_fir_filter_impl.cc), [SoapySDR streaming format/ownership](https://pothosware.github.io/SoapySDR/doxygen/latest/classSoapySDR_1_1Device.html). No source code was copied.
