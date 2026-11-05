# SkyRoof-Plus v1.66: P0–P3 software release acceptance (2026-11-05)

## Scope and decision

**Software maintenance release candidate: ACCEPTED subject to a clean release workflow and the shipped installer's signature check.** This is a **risk-based software-release gate**, **not** a declaration that the repository has zero historical findings, that all optional refactors are finished, or that physical hardware tests passed. Any remaining governance work stays tracked after the release.

Baseline: aislop v0.18.1 on 2026-11-04 had 102 errors / 855 warnings.
Latest independently scanned `master` is `ca9c6d24661c53dff87c07c300c886deee747bd9`.

- [Windows x64 Compile Check, master](https://github.com/Iu-yang1/SkyRoof-Plus/actions/runs/41995719505): **PASS**, zero build errors, **1193/1193 xUnit passed**, CW R2C FFT benchmark on 256/2048/3840/4096/8192/11520 samples, test installer produced and runtime dependency payload verified (60 root DLLs, 4 RID assets).
- [Pinned aislop 0.18.1, master](https://github.com/Iu-yang1/SkyRoof-Plus/actions/runs/41995719263): **PASS baseline gate**, complete report **64/100**, 781 findings: **1 error, 779 warnings, 1 info; no new unbaselined findings**. The CLI label is *Needs Work*, not *clean source*. No lint or security-engine diagnostics in this scan; that does not prove there are no vulnerabilities.
- The baseline has **not** been automatically regenerated to suppress findings.

## P0: fault containment and safety invariants

- Existing merged reviews [#152](https://github.com/Iu-yang1/SkyRoof-Plus/pull/152), [#153](https://github.com/Iu-yang1/SkyRoof-Plus/pull/153), [#155](https://github.com/Iu-yang1/SkyRoof-Plus/pull/155)–[#162](https://github.com/Iu-yang1/SkyRoof-Plus/pull/162) retain diagnostics for rotator IPC/STOP, SkyCAT/CAT, WASAPI, ONVIF, CW, FT4, SSTV and telemetry. No motor command, CAT/PTT ownership or protocol change is accepted as a score-only fix.
- Regression coverage includes `NativeRotatorFeedbackPolicyTests`, `NativeRotatorWorkerTests`, `NativeRotatorEnduranceTests`, `SstvPttReleaseTests`, `SstvTxAbortTests`, `SstvVirtualCatIntegrationTests`, `KissServerLifecycleTests`, and `CatCapabilitiesRecoveryTests`.
- The only remaining scanner **error** is `SkyRoof/ExceptionLogger.cs:184` (`ai-slop/swallowed-exception`). This is a deliberate no-throw SoapySDR native callback boundary, reviewed in [the exception record](aislop-native-callback-exception-20261105.md). Removing the catch or adding reentrant logging would be unsafe. Callback failure-injection and a bounded no-throw diagnostic counter/channel remain **unverified / follow-up**, not silently waived.

## P1: null and async lifecycle

- [#164](https://github.com/Iu-yang1/SkyRoof-Plus/pull/164) removes AMSAT UI-thread blocking HTTP waits, cancels requests at dialog teardown and tests encoding/UTC reporting boundaries.
- [#165](https://github.com/Iu-yang1/SkyRoof-Plus/pull/165) replaces 26 unsafe null-forgiving accessors to satellite/transmitter tuning customization with explicit fail-before-mutation checks, with three tests preserving configured values and rounding.
- **240 other null-forgiving warnings** remain in historical baseline. They must not be declared resolved merely because the analysis gate passed. The remaining 12 sync-over-async, 2 async-void and 14 syntactic unreachable-code indicators require context-aware treatment: some are dedicated synchronous worker boundaries, completed-task reads, WinForms event callbacks or heuristic false positives. Further targeted changes should use regression tests, not blanket substitutions.

## P2: AFC / IF→AF / CW / SSTV / CAT / rotator separation

- [#154](https://github.com/Iu-yang1/SkyRoof-Plus/pull/154) separates AFC observation, polarity and servo responsibilities without changing control law.
- Important independent numeric/control checks: `IfAfOptimizedDspTests.FourPhaseRealFirMatchesIndependentComplexReference`, `IfAfSimulatedHardwareTests`, `CwRealFftTests.R2cMatchesMathNet_UnnormalisedAmplitudeAndComplexPhase`, `AfcAlgorithmTests` (CAT timestamp alignment, polarity validation, PTT hold, probe rollback), and rotator feedback/reconnect tests.
- The six-length CW FFT benchmark completed successfully in the master run. Its measured ratios are specific to that CI runner and are **not** a cross-CPU or end-to-end CW decoding performance guarantee.
- Additional file-size / long-method warnings (**54 / 138**) and optional decoupling remain maintainability debt, not proof of a regression or of completed architectural modernization.

## P3: repeatable CI, code review and release

- [#159](https://github.com/Iu-yang1/SkyRoof-Plus/pull/159) runs CW R2C FFT benchmark on master pushes; Windows compile/xUnit/installer and pinned `aislop@0.18.1` remain independent checks.
- v1.66 release workflow **requires a pinned aislop v0.18.1 Ubuntu quality job before starting the Windows release build**, independently runs the six-size CW R2C FFT benchmark and uploads its JSONL evidence before publishing; it additionally refuses pre-existing tags, serializes releases, validates installer Authenticode when SignPath is configured, and marks unsigned builds prerelease. Never overwrite `v.1.66` or reuse an earlier tag.
- Remaining style/code-review warnings include 206 narrative comments, 46 index loops, 38 too-many-parameters and 15 trivial comments; comments that preserve safety or DSP invariants must not be deleted for the scanner score alone.

## Physical and post-publish exclusions

Not demonstrated by GitHub Actions: actual motor/shaft STOP and C2 feedback provenance, unplug/reconnect of the rotator controller, IC-9700 CAT/PTT lease correctness under RF transmission, WASAPI/USB audio hot-unplug, real ONVIF camera teardown, SoapySDR callback fault injection, complete over-the-air CW/FT4/SSTV sensitivity and AFC error bounds.

**Release publish acceptance:** after PR merge, verify `Directory.Build.props` has 1.66, release workflow success on that exact master commit, tag `v.1.66` points to its SHA, `SkyRoofSetup-v.1.66.exe` downloads, SHA-256 is recorded, and signing/unsigned prerelease status matches actual Authenticode inspection. On any failure, do not claim a completed release. Keep the governance automation enabled while historical/physical items remain unresolved.

## Publication and post-verification follow-up

- `v.1.66` was created on `50525f8c238c4948579665667df766a44870d499` with `SkyRoofSetup-v.1.66.exe` (33,536,572 bytes), public [release page](https://github.com/Iu-yang1/SkyRoof-Plus/releases/tag/v.1.66).
- GitHub's recorded installer digest is `sha256:424b6a59924503f1230c5a5d0e50a8dde6188ddcfc24a4770eca2a6332a30f82`. SignPath was unavailable and the Windows release build reported `NotSigned`; the release is therefore **prerelease=true**, not a signed stable release.
- [Build and Release #41997741066](https://github.com/Iu-yang1/SkyRoof-Plus/actions/runs/41997741066) passed pinned quality, Windows compile, **1193 xUnit**, six FFT measurements, packaging, VB-CABLE and installer checks, and published its asset. Its final verification step **failed** because PowerShell parsed `$tag:refs` as a scoped variable in a Git refspec. This is a verification-script defect, **not evidence of a broken installer**; nevertheless, the workflow as a whole must be reported **failed**.
- Subsequent changes fix the refspec for future releases and independently re-download/hash-check the already-published v1.66 via `.github/workflows/verify-release-integrity.yml`. This verification is required before describing v1.66 as post-publish integrity-verified. Do not rewrite or reuse the existing tag or asset.

