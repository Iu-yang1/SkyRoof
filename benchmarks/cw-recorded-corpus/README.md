# Recorded CW corpus

This directory defines the input contract for SkyRoof's recorded-audio CW
benchmark. Actual recordings are intentionally not committed to the repository.

## What is measured

The runner feeds each WAV through the same receive stages used by the CW
Console:

1. frame-level ridge scan;
2. bounded fixed-lag multi-frame association;
3. labelled Kalman/GNN tracking;
4. the production wideband activity-aware DeepCW separator;
5. the pinned DeepCW ONNX/CTC model;
6. incremental transcript reconciliation.

The manifest's `referenceFrequencyHz` is **not** supplied to detection,
tracking, separation or decoding. It is used only after decoding to match an
observed output lane to the corresponding truth lane for scoring.

Per-lane output contains lane match status, observed median AF frequency,
decoded text, CER, WER and optional callsign recognition. Per-case output also
contains real-time factor (RTF). The summary reports lane recall, mean CER/WER,
callsign rate and the worst case RTF.

## Corpus layout

A corpus ZIP/root must contain:

```
manifest.json
recording-1.wav
recording-2.wav
...
```

Start from [manifest.example.json](manifest.example.json) and validate against
[manifest.schema.json](manifest.schema.json).

WAV may be mono or multichannel PCM/IEEE-float. Multichannel audio is averaged
to mono by the benchmark. Each case must be at least as long as its
`decodeWindowSeconds` value.

For a recording with moving CW carriers, `referenceFrequencyHz` should be a
representative/median AF position used only to associate the final transcript
with truth. Set `frequencyToleranceHz` wide enough to cover the expected
recording drift without overlapping another truth lane. A common deterministic
Doppler rate can be supplied with `knownDopplerRateHzPerSecond`; residual
station drift is still estimated by the tracker.

## Run locally

From a Windows PowerShell prompt:

```powershell
./scripts/run-cw-corpus.ps1 -CorpusRoot C:\cw-corpus
```

The script uses the same pinned DeepCW revision as SkyRoof and writes a JSON
report under `artifacts/`.

## Run in GitHub Actions

Use the manual **CW Recorded Corpus Benchmark** workflow. Supply:

- an HTTPS URL to a ZIP containing the corpus root;
- the ZIP SHA-256.

The workflow refuses non-HTTPS downloads and refuses a hash mismatch. The WAV
files are not committed to SkyRoof and the JSON metrics are uploaded as a run
artifact.

## Recording guidance

For comparisons across versions, preserve the original unprocessed receiver
audio. Do not normalize, denoise or band-pass a file differently for different
SkyRoof revisions. Record enough leading/trailing audio for track birth,
fixed-lag association and incremental transcript confirmation.

Useful corpus axes include:

- 5/10/15/25/40 Hz carrier spacing;
- 0/6/12 dB near/far imbalance;
- 10/20/30/40 WPM;
- fading and key-up gaps;
- 0–20 Hz/s satellite Doppler/rate error;
- real QRM, receiver noise and AGC behavior.

Do not put private callsign/operator material in a public corpus unless you have
permission to publish it.
