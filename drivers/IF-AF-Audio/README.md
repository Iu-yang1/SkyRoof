# IF-AF-Audio virtual microphone and low-latency link

**Implementation status:** The SkyRoof user-mode low-latency receiver pipeline and UI status strip are implemented on the IF-AF-Test branch. A genuine Windows kernel virtual audio driver has **not yet been built, signed or deployed**. An in-process queue is not a Windows MMDevice microphone.

## Driver specification (future WDK project)

Fork the official [Microsoft SysVAD WaveRT sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-driver-samples/sysvad-virtual-audio-device-driver-sample/). Implement a paired virtual device:

- Playback/render: **IF-AF-Audio Playback** (SkyRoof writes the demodulated 48-kHz mono float AF).
- Recording/capture: **IF-AF-Audio** (WSJT-X and other clients choose this as their Input).
- Driver ring: timestamped WaveRT position/QPC sample clock, bounded producer/consumer indices, proper silence on underrun, no replay of stale audio, multi-client capture, and safe shutdown. Never expose Icom USB TX codec as this sink.
- WDK build, HLK/compatibility testing and Microsoft Hardware Dev Center signing are required for a production package. Do not disable Secure Boot or kernel integrity protection for routine installation. No automated kernel driver installation by SkyRoof in this phase.

## User-mode changes in this branch

Settings → 12-kHz IF → AF: `AudioRoute` selects the default backward-compatible `ExistingVirtualCable` mode, or `PreferIfAfAudio` which **requires both dedicated Windows endpoints actually installed**. The read-only `DriverStatus` reports availability; no fake microphone is registered.

`LatencyPreset`: `Fast20Ms`, `Balanced40Ms` (default), `Stable80Ms`, or `Compatible200Ms`. WASAPI render requests that period; capture requests 20 ms, reader frames are 480 samples (10 ms); the bounded DSP processing queue holds 8 such frames (~80 ms worst pending queue), dropping overflow rather than building unbounded latency. Existing audio routes retain their original defaults.

SkyRoof status strip: **IF › DDC › AF › V-MIC/VAC**, with `Q` = estimated host processing queue age, `P` = requested WASAPI output period, plus clickable runtime diagnostics. Neither is actual hardware-to-WSJT-X end-to-end latency, which must be measured separately.

References: [SysVAD](https://github.com/microsoft/Windows-driver-samples/tree/main/audio/sysvad), [Windows low latency audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio), [driver signing requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings).
