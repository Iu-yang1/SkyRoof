# SkyRoof SSTV — Windows software acceptance

This is the release checklist for the integrated SSTV console, based on Windows x64 CI and an in-process virtual IC-9700/SkyCAT server. It replaces the former hardware bench checklist.

## Automated gates

| Area | Test suite / evidence | Expected outcome |
|---|---|---|
| Native and extended modes | `SstvAllModeVisTests`, `SstvExtraModeTests`, `SstvRasterFamilyRoundTripTests`, `SstvRgbWaveExporterTests` | 43-mode encoding and VIS framing; reproducible raster tests |
| Tone, noise, IF | `SstvWeakSignalVisTests`, `SstvIfAudioDemodulatorTests`, `SstvNativeLineSyncAfcTests` | Frequency compensation and VIS acquisition under synthetic impairment |
| Audio file | `SstvWaveExporterTests`, `SstvWaveReceiverTests`, `SstvWavNormalizerTests` | WAV framing, formats and normalization |
| Virtual CAT + STOP | `SstvVirtualCatIntegrationTests`, `SstvPttReleaseTests` | Real CAT worker drives a fake IC-9700 over loopback TCP; STOP survives lost ACK/reconnect; PTT OFF readback |
| Satellite/Doppler | `SstvTxPolicyTests`, `SstvTxAbortTests` | Changed uplink model accepted while valid; mismatched state aborts |
| Operator UI | `SstvOperatorUiSmokeTests` | RX/TX/gallery/QSO, image editing, armed/unarmed states, 980×720 and 650×420 snapshots |
| Installer | Windows Compile Check workflow | Release x64 solution builds, test suite passes, package and dependency validation succeeds |

## Release procedure

1. Run full **Compile Check** and **Docs Validation** on the exact PR HEAD; inspect the SSTV UI screenshot and virtual CAT test results.
2. On passing checks, merge the PR into `master`. Bump `Directory.Build.props` to a new unused version.
3. Run CI on the merge commit and manually dispatch the `Build and Release` workflow against `master`.
4. Verify that the published GitHub Release contains the versioned installer and correct changelog. An unsigned installer is published as a pre-release per the repository's signing policy.

The virtual CAT server maintains a simulated PTT state across TCP reconnections, including an injected lost `T 1` reply. CI tests exercise the software command flow and do not interact with a physical transceiver.

Reference: [PR #130](https://github.com/Iu-yang1/SkyRoof/pull/130) and [integration notes](sstv-console-integration-plan.md).
