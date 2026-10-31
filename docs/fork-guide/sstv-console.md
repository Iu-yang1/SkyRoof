# SSTV Console (Development Preview)

> **Not a release.** This section documents the SkyRoof draft pull request [#130](https://github.com/Iu-yang1/SkyRoof/pull/130). It does not certify on-air TX or full MMSSTV compatibility.

## Open the SSTV workspace

Choose **View → SSTV RX / TX**. The workspace has three user-facing pages: **Receive**, **Transmit**, and **Gallery**. Dock it beside the satellite waterfall or use it as a floating window.

### Receive a live signal

1. Select the AF capture endpoint, RS-BA1 playback-loopback endpoint, or real **12 kHz IF** endpoint. The selected endpoint must contain the actual radio receive audio.
2. Click **Start Receive**, then tune your receiver normally. Select **Stop Receive** when finished.
3. If the whole SSTV AF spectrum is consistently shifted, set the manual **Hz** correction (±500 Hz). This changes RX audio only; it does not retune radio CAT or uplink frequencies.
4. The current SkySignals backend automatically recognizes Robot 36/72 and PD 50/90/120/160/180/240/290 where decoding confidence permits. The decoder may not recognize other MMSSTV modes.
5. Use **Import WAV** to decode a 48 kHz / 16-bit mono WAV (max seven minutes). Completed pictures are archived with UTC metadata under the user-data `SstvImages` folder.

**Important:** Loopback captures the entire selected Windows playback endpoint, not exclusively the RS-BA1 process. A dedicated playback endpoint avoids other applications mixing in.

### Prepare an image

Open a PNG/JPEG or paste one from the clipboard. The preview resizes the image to the selected mode and can overlay your callsign or a short message. Export **SSTV WAV** to test the waveform without transmitting. Robot/PD waveforms contain standard 8-bit VIS headers and continuous-phase image audio.

### Experimental on-air transmission

The draft branch has a guarded TX implementation; **do not treat it as an IC-9700-tested release**. The TX button requires explicit user approval, an above-horizon satellite with a 2 m/70 cm amateur uplink, USB or FM mode, a working TX CAT backend that reports its **actual TX frequency and hardware PTT state**, and a dedicated playback endpoint routed to the radio transmit audio input.

PTT is requested only after WAV generation and WASAPI initialization. The code then waits for a fresh hardware PTT acknowledgement, checks the expected TX CAT frequency, and transmits. A frequency mismatch, loss of satellite observation, PTT readback timeout, audio failure, duration limit, STOP, or window close requests ownership-aware PTT release. CAT Doppler updates are not frozen for the entire image.

**Before any RF test:** verify the physical wiring and audio injection route into the radio; start with a dummy load, low power, and a spectrum/monitor receiver; confirm PTT and output after STOP. Remote application PTT readback is not equivalent to measuring RF energy or spectral purity. Never assume a failed CAT ACK proves the transmitter is unkeyed.

## Current compatibility boundary

- RX: Robot 36/72 + PD family using the existing SkySignals decoder; 12 kHz real-IF frontend requires one clean sideband.
- TX: Robot 36/72 + PD family, plus Scottie 1/2/DX, Martin 1/2 and SC2 60/120/180 RGB family, 48 kHz mono WAV and experimental guarded on-air playback. RGB modes use timing and VIS definitions audited against MMSSTV Main.cpp. **This addition does not extend RX compatibility.**
- Still missing: Martin/Scottie/SC2 **RX**, AVT/P3/P5/P7/MP/MR/ML/extended/N-VIS receive-and-transmit parity, full image editor/templates/macros, automated VIS/sync-driven SSTV AF AFC, independent IC-9700 bench verification, and dedicated long-run on-air fault-injection tests.

No stable Release should be made until the source-to-target compatibility matrix, CAT/PTT real-radio checks, and production acceptance pass.

## Source and licensing

- [MMSSTV protocol reference](https://github.com/n5ac/mmsstv)
- [SkySignals SSTV decoder](https://github.com/VE3NEA/VE3NEA.SkySignals)
- [SSTV integration and acceptance plan](../architecture/sstv-console-integration-plan.md)

SkyRoof remains AGPLv3; incorporate upstream material only with corresponding attribution and license compliance.
