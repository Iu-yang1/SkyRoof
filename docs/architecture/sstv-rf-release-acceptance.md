# SkyRoof SSTV P0–P4 RF release acceptance (evidence required)

**Status: NOT APPROVED FOR ON-AIR RELEASE.** This is the acceptance procedure and evidence ledger, not evidence that it has been executed. PR #130 must stay a draft until every critical gate below is verified. Passing Windows CI, an internally generated SSTV loopback, a CAT command ACK, or a WinForms screenshot is **not** an RF safety certification.

## A. Software gates (no radio keyed)

- [ ] Build, all automated tests, documentation validation and installer integrity pass **on the exact release commit**; keep links to that SHA and workflow runs.
- [ ] Each of the 43 named SSTV modes: generate a canonical clean 48-kHz mono WAV, detect VIS/N-VIS, decode the image, and compare against **independently produced MMSSTV reference WAV and reference image**, not a SkyRoof self-loop alone.
- [ ] Lock mode data against published `n5ac/mmsstv/mode.txt` and `EMMSSTV.TXT`. In particular, **MC110-N has TW=143 ms** (not 140 ms); verify full 256-line duration and header.
- [ ] Record per-mode checksum/fixture provenance, header timing, raster dimensions, component order, sync timing, end-of-image identification, and image-quality metrics. For Robot BW compare monochrome luma, not RGB PSNR.
- [ ] Replay AWGN, QSB, frequency offset, clock skew/slant, fading, lost VIS and mid-image acquisition across standard, extended and narrow modes. Publish input conditions and images rather than claiming an unmeasured SNR threshold.
- [ ] Verify 12-kHz IF USB **and** LSB demodulation, AF audio, RS-BA1 loopback and capture-device removal. An IF waveform must not be interpreted as baseband AF.
- [ ] Run WinForms interaction tests and manually check 100%, 125%, 150%, and 200% Windows DPI, keyboard focus/navigation, all RX/TX pages and high-contrast legibility.
- [ ] Confirm no auto-CQ, no unsolicited TX on program start/restart, and no default Windows speaker auto-selected as the SSTV RF audio route.

## B. RF bench preconditions (strict)

1. An RF-trained operator is physically beside the IC-9700, with an immediately accessible **radio-front-panel PTT/TX stop, power-off**, and ability to remove the audio source. CAT stop over a disconnected network is not an independent fail-safe.
2. Connect the actual TX antenna port to a **proper 50-ohm dummy load rated for the selected power and duty cycle**. Use a rated directional coupler / attenuator before any spectrum instrument; do not connect transmitter RF directly to a receiver, SDR, or spectrum analyzer input.
3. Set the transceiver to its lowest practical RF power, verify the actual TX path (USB audio / ACC / RS-BA1 LAN input) and software output device. Check microphone/ACC/LAN selection and ALC independently. Keep the RX route separate from the selected TX route to avoid feedback.
4. Use a currently permitted uplink/mode and valid satellite/observation model for the existing software interlock. **Never override LOS, amateur-band, or CAT readback guards just to perform a test.** No antenna radiating before bench acceptance.
5. Record hardware/firmware versions, SkyCAT/RS-BA1 version, radio configuration, SHA, output endpoint ID, load rating, RF power, DUT clock reference, local and UTC date/time.

## C. IC-9700 + RS-BA1 supervised fault-injection matrix

| Test | Expected observable result | Evidence / actual result |
|---|---|---|
| Export PNG to SSTV WAV with no radio | No CAT PTT activity and no RF | NOT RUN |
| Open console and choose a mode/image | TX audio endpoint stays unselected and PTT stays RX | NOT RUN |
| Attempt TX without operator approval | PTT remains RX | NOT RUN |
| Select wrong/no CAT frequency or invalid uplink | Rejected before key-up | NOT RUN |
| Valid low-power dummy-load TX | PTT ON confirmed by fresh CAT read; intended audio reaches radio and has measured expected RF modulation | NOT RUN |
| STOP during WAV generation | No PTT ON | NOT RUN |
| STOP during pre-roll and mid-image | Audio stops; application PTT OFF requested and hardware RX read observed; measure RF cessation | NOT RUN |
| Unplug selected audio endpoint mid-TX | Playback fault triggers termination; CAT RX and RF cessation independently measured | NOT RUN |
| Disconnect CAT after PTT ON | Persistent unconfirmed-PTT alarm, no new SSTV keying; **operator manually ensures RF cessation** | NOT RUN |
| CAT OFF ACK but absent hardware RX readback | Do not report safe completion; latch and block re-arm | NOT RUN |
| Restore CAT with fresh PTT OFF readback | Fault may clear only after hardware RX evidence; **new intentional approval** required | NOT RUN |
| Change selected satellite, mode, uplink validity or LOS | TX abort, PTT release attempted and independently checked | NOT RUN |
| Active 2–5 min image with uplink Doppler updates | CAT follows current orbital model without SSTV RX AFC writing TX; no false offset/frequency takeover | NOT RUN |
| Another client already owns PTT | SSTV cannot claim ownership or unkey the external owner | NOT RUN |
| Close SSTV window, close application, force exception | No stuck RF output; failure mechanisms and manual recovery documented | NOT RUN |
| Repeated TX/RX cycles and several soundcards | No deadlock, audio route change during TX, unbounded buffers or memory growth | NOT RUN |

A single critical PTT/LOS/band/ownership failure is a **NO-GO**. If hardware RX readback is unavailable, record the test as *not demonstrated*, never assume RF OFF from a successful command ACK or a silent PC audio stream.

## D. Acceptance record, evidence and decision

For each run collect: commit SHA; operating configuration; operator and UTC time; RF power and spectrum screenshot (with attenuation); CAT TX frequency and PTT timeline; RS-BA1 audio routing screenshot; WAV sample; decoded reference image; injected fault and recovery result; final YES/NO. Store links to the evidence next to the matrix entries. Avoid publishing personal audio-device serial numbers or secrets.

**Release decision: NO-GO until** the software corpus, human UI acceptance, supervised dummy-load matrix, satellite tracking checks and independent RF off measurements are signed off. Only then merge the draft PR to `master`, run *fresh master-SHA checks*, build and verify the installer, and publish an appropriately labeled Release. A pre-release developer build may be used for offline WAV and bench tests with prominent RF warnings; it is not a QSO-ready stable version.

Reference: [n5ac/mmsstv mode.txt](https://github.com/n5ac/mmsstv/blob/master/mode.txt), [the integration plan](sstv-console-integration-plan.md), [PR #130](https://github.com/Iu-yang1/SkyRoof/pull/130).
