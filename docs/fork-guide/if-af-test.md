# IF→AF Receiver: IC-9700 to WSJT-X

SkyRoof provides an optional 48 kS/s **real (non-I/Q) 12-kHz USB IF → SSB/CW/FM → mono AF → virtual cable** receive chain. The demodulator never routes receive audio to Icom transmit endpoints or commands CAT/PTT. Separately enabled AFC is bounded to RX-only frequency corrections.

## Setup

1. IC-9700: configure **USB AF/IF Output = IF**, Windows USB capture at 48 kHz, and disable Windows microphone enhancements.
2. SkyRoof Settings → **12 kHz IF → AF / WSJT-X**: select the USB capture endpoint, correct Left/Right channel and FollowRadio or a fixed demodulation mode. FollowRadio follows SkyRoof downlink/RX state.
3. Installer optionally offers original basic VB-CABLE, or use an already installed vendor cable. AutoSignedCable pairs the VB-CABLE playback endpoint **CABLE Input** with recording **CABLE Output** by name (not cryptographic driver verification). For a manual cable, select a dedicated render endpoint. **Never route to Speakers (USB Audio CODEC)** or another radio TX source.
4. Enable the receive route. WSJT-X → Audio Input → **CABLE Output**. Do not simultaneously route SkyRoof SDR Output Stream to that same cable. Restart after vendor driver installation if requested.
5. IF/DDC/AF/V-MIC status indicators show receiver health; clicking displays concise input/output samples, levels, dropped blocks, underruns, and actionable errors. Green V-MIC is **not proof of successful WSJT-X decoding**.

## DSP

- 12-kHz quarter-rate NCO; 257-tap complex SSB FIR selecting USB/LSB (1.2–3.3-kHz nominal passbands); 700-Hz CW tone with 100–1000-Hz filters.
- FM: 129-tap complex IF FIR, quadrature phase discriminator, DC blocker, 95-tap AF FIR. 6.25-kHz preset: ~3.0/2.1-kHz IF/AF cutoffs and 900-Hz deviation normalization. Nominal 12.5-kHz preset: ~5.5/3.0 kHz and 2.5-kHz deviation. These are not rectangular masks.
- Independent FM trim, optional voice-only de-emphasis and squelch (default OFF), bounded SSB/CW slow AGC and peak protection. Raw IF is shared with AFC/SSTV for matching sources; CW is not automatically a raw-IF consumer.
- 48-kHz capture, 480-sample processing frames, bounded eight-frame queue and 20/40/80/200-ms selectable requested WASAPI render buffering. This is **not measured hardware or end-to-end WSJT-X latency**.

## Reliability and AFC

Decoder startup failures now surface visibly rather than keeping capture active with silent AF. Missing PCM frames reset streaming filter state. On sustained drops, raise output buffer latency or reduce host CPU contention. The SO-50 weak-FM AFC gate requires three time-separated consistent candidate observations (>=80 ms apart), and default HoldLast preserves the last verified RX residual correction during signal loss while orbital Doppler continues. TX/PTT interlocks remain unchanged.

## Distribution and testing limits

Only original unmodified **basic VB-Audio VB-CABLE** Donationware is eligible for optional installer bundling. Attribution, donation opportunities and license rules: https://vb-audio.com/Services/licensing.htm and https://vb-cable.com/. Professional/organizational use must follow vendor licensing. No experimental unsigned self-developed SysVAD driver ships.

Synthetic xUnit tests and Windows CI do not establish real IC-9700 analog sensitivity, end-to-end device latency or WSJT-X decode equivalence. The experimental USB IF + LAN AF dual-track comparison recorder is not included in production.
