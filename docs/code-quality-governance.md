# SkyRoof-Plus code quality governance

## Static-analysis baseline

- Pinned CLI: `aislop@0.18.1`, same version as the 2026-11-04 review.
- The stored `.aislop/ci-baseline.json` is an **accepted historical debt inventory**, never a claim that the findings have been fixed.
- Each PR must avoid **new** unbaselined diagnostics. Existing accepted issues remain visible in the full-source JSON Artifact and are removed with `aislop baseline prune` only after their fixes are tested.
- A human-readable summary and the complete JSON report are uploaded by the independent Ubuntu `aislop Quality Gate` job.
- cppcheck and ruff are installed explicitly. Check the full report for engines actually evaluated; a zero issue count for a skipped engine does not imply code safety.

## High priority remediation

1. P0: classifying swallowed exceptions and fixing task lifecycle (KISS, AFC, CAT, ONVIF).
2. P1: null-state checks and configuration parsing; refactoring AFC interfaces with golden IF/CAT comparisons.
3. P2: reduce cross-module complexity without changing sample rates, FFTs, servo gains, CAT/PTT command ordering or rotator interlocks.
4. P3: remove low-value comments where safe; preserve engineering invariants and real-time numeric loops.

**Rule exception policy:** A false positive must be recorded with file, line, invariant, rule and regression evidence. Do not globally disable categories to inflate the score.

## CI and release acceptance

Windows `Compile Check`/xUnit, optional native benchmarks, simulated CAT/rotator scenarios, package verification and this Linux static report are **separate** checks. Do not merge a PR that has a reproducible failed gate. Hardware-level latency, CW decode rate, AFC accuracy and physical rotator movement require explicit measurements rather than CI-only claims.

The temporary bootstrap workflow generated the initial baseline only on an isolated branch. Do **not** run baseline write on every pull request: that would accept regressions automatically.
