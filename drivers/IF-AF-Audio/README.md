# IF-AF-Audio — Native WaveRT development implementation

## Status

This experimental driver *source* is now built from the pinned Microsoft
SysVAD sample and an MIT-licensed cable implementation developed by
SeventhSG/LoopIt7. The kernel ring code is real, not a .NET pseudo-microphone.
**No production signature, device installation, live round-trip measurement,
or HLK acceptance is claimed by the presence of this source.**

### Architecture

- `IF-AF-Audio Playback`: Windows render endpoint, SkyRoof's only producer.
- `IF-AF-Audio`: Windows recording endpoint, WSJT-X's capture input.
- Raw transfer format: **48,000 Hz / 1 channel / IEEE float32** on both WaveRT pins.
  Ring `blockAlign=4`, `bytesPerSecond=192000`; no sample-rate conversion in kernel.
- Shared `CCableRing`, owned by the SysVAD adapter; spin-lock protected,
  nonpaged DPC-safe write/read; 500 ms bounded capacity, initial ~30 ms preroll,
  independent capture cursors, zero fill on underrun, discard overwritten
  samples, and reset on playback transition.
- One playback writer at a time, multiple capture readers; no source audio
  copying to disk, no direct radio CAT/PTT/TX routing, no auto-install.
- Dedicated `ROOT\IfAfAudio` identity, separate service, binary, PnP names
  and custom category GUIDs (not the LoopIt7 or upstream device).
- The existing SkyRoof `PreferIfAfAudio` route still fails closed when the
  actual endpoints are not installed.

**Trust boundaries:** The host's shared-mode Windows audio engine may still
perform conversion for third-party clients. No audio format conversion or
hardware latency guarantee exists inside this WaveRT cable. The driver
cannot itself prove a WSJT-X decode.

## Reproducible source and build

```powershell
# Run on Windows 11 with VS2022, SDK and WDK 10.0.26100
./drivers/IF-AF-Audio/scripts/fetch-sample.ps1 -Force
./drivers/IF-AF-Audio/scripts/build.ps1 -Configuration Release -Platform x64
```

`fetch-sample.ps1` clones **Microsoft Windows-driver-samples**, pinned to
`67d81f217bc01edf7a4320e4911c11065635acfa`, applies the unmodified
`patches/loopit7-upstream.patch` (from
[SeventhSG/LoopIt7](https://github.com/SeventhSG/LoopIt7/tree/main/driver)
under MIT), and runs `scripts/overlay.py` with strict expected-source checks.
The upstream patch is intentionally preserved unaltered for provenance.
Build outputs are produced into ignored `vendor/` and never committed.

`build.ps1` does a clean rebuild of both the SysVAD common library and
TabletAudioSample driver, runs `InfVerif` and `Inf2Cat`, and stages
**development-only** `IfAfAudio.sys`, `IfAfAudio.inf` and `ifafaudio.cat`.
CI uses the Windows 2022 GitHub-hosted runner with VS2022 + WDK and uploads
a development-only artifact (WDK may auto-test-sign the SYS, but the package has no Microsoft release signature). CI does not create a release,
enable test-signing, disable Secure Boot, or install an unsigned driver.

## Validation gates

1. **Build / INF / catalog:** GitHub Actions `IF-AF-Audio WDK Driver`.
2. **Isolated driver-host VM:** install a properly signed development build
   under an authorized driver-testing environment; verify endpoint
   enumeration and render→capture signal integrity with 1 kHz, noise,
   discontinuous bursts and silence.
3. **Stress:** multiple recording clients, producer dropout, 20/40/80 ms
   periods, rapid device start/stop, DPC/IRQL Driver Verifier, playback
   restart without stale samples, device removal.
4. **RF integration:** capture IC-9700 IF, decode AF into the new playback
   endpoint; select `IF-AF-Audio` in WSJT-X and verify decoded FT4/FT8.
5. **Public distribution:** only after Microsoft Hardware Dev Center
   signing (attestation / WHQL as applicable) and acceptance testing.
   Open-source status does **not** eliminate Windows kernel signature checks.

Do not install this development-only CI artifact on a daily-use host, change boot
security settings to bypass checks, or ship it with the normal SkyRoof setup.

### Provenance and licensing

- Original sample: Microsoft Windows-driver-samples / audio/sysvad — MIT.
- Cable patch: SeventhSG/LoopIt7 — MIT (license copied to
  `LICENSE-LoopIt7-MIT.txt`), with minor SkyRoof-specific changes applied
  **after** the original patch.
- SkyRoof-specific overlay: part of this repository's project.

### Remaining limitations

There is no signed driver installed on a real Windows host and no audio
loopback or HLK result yet. Review the first successful WDK pipeline results,
then test on an isolated VM with a test-authorized signature. Further fixes
may be required after Driver Verifier, WaveRT position and sleep/resume tests.
