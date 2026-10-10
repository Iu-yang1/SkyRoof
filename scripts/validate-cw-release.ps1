param(
  [switch]$DocsOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

function Require-File([string]$relative) {
  $path = Join-Path $repoRoot $relative
  if (-not (Test-Path $path -PathType Leaf)) {
    throw "Required CW release file is missing: $relative"
  }
  if ((Get-Item $path).Length -le 0) {
    throw "Required CW release file is empty: $relative"
  }
  return $path
}

$required = @(
  "docs\users_guide\cw_console.md",
  "docs\zh-cn\users_guide\cw_console.md",
  "docs\users_guide\toc.yml",
  "docs\zh-cn\users_guide\toc.yml",
  "benchmarks\cw-recorded-corpus\README.md",
  "benchmarks\cw-recorded-corpus\manifest.schema.json",
  "benchmarks\cw-recorded-corpus\manifest.example.json",
  "scripts\run-cw-corpus.ps1",
  ".github\workflows\cw-recorded-corpus.yml",
  "licenses\DeepCW-NOTICE.txt",
  "licenses\HamNoise-NOTICE.txt",
  "licenses\AGPL-3.0.txt"
)

foreach ($relative in $required) {
  Require-File $relative | Out-Null
}

$schemaPath = Require-File "benchmarks\cw-recorded-corpus\manifest.schema.json"
$schema = Get-Content $schemaPath -Raw | ConvertFrom-Json
if ($schema.properties.version.const -ne 1) {
  throw "Recorded CW corpus schema version must remain 1 until an explicit migration is implemented."
}

$examplePath = Require-File "benchmarks\cw-recorded-corpus\manifest.example.json"
$example = Get-Content $examplePath -Raw | ConvertFrom-Json
if ($example.version -ne 1 -or $example.cases.Count -lt 1) {
  throw "Recorded CW corpus example is not a valid v1 example."
}

$englishGuide = Get-Content (Require-File "docs\users_guide\cw_console.md") -Raw
$chineseGuide = Get-Content (Require-File "docs\zh-cn\users_guide\cw_console.md") -Raw
foreach ($needle in @("Shift+F1", "SENDHZ", "DeepCW", "STOP")) {
  if (-not $englishGuide.Contains($needle)) {
    throw "English CW guide is missing required safety topic: $needle"
  }
  if (-not $chineseGuide.Contains($needle)) {
    throw "Chinese CW guide is missing required safety topic: $needle"
  }
}

if ($DocsOnly) {
  Write-Host "CW docs/corpus release validation passed."
  exit 0
}

$deepCw = Get-Content (Require-File "SkyRoof\CW\DeepCwModel.cs") -Raw
$expectedRevision = "8e264d243bbd4467bd19f3f28292219405b47e0e"
if (-not $deepCw.Contains("public const string Revision = `"$expectedRevision`";")) {
  throw "DeepCW revision changed without updating the release audit: expected $expectedRevision"
}

$deepNotice = Get-Content (Require-File "licenses\DeepCW-NOTICE.txt") -Raw
if (-not $deepNotice.Contains($expectedRevision) -or
    -not $deepNotice.Contains("AGPL-3.0-only")) {
  throw "DeepCW notice does not match the pinned model revision/license."
}

$settings = Get-Content (Require-File "SkyRoof\Settings\CwConsoleSettings.cs") -Raw
if ($settings -notmatch '(?s)\[DefaultValue\(true\)\].{0,300}public\s+bool\s+TransmitEnabled\s*\{[^}]*\}\s*=\s*true\s*;') {
  throw "CW TransmitEnabled must remain explicitly default-true; actual RF still requires explicit Arm TX and SkyCAT preflight."
}

$iss = Get-Content (Require-File "install\SkyRoof.iss") -Raw
if ($iss -match 'hamnoise_skyroof\.dll') {
  throw "Benchmark-only HamNoise DLL must not be shipped by the production installer."
}
if ($iss -notmatch 'Source:\s*\.\.\\licenses\\\*') {
  throw "Production installer no longer packages the licenses directory."
}
foreach ($runtimeFile in @(
    "Microsoft.ML.OnnxRuntime.dll",
    "System.Numerics.Tensors.dll",
    "runtimes\win-x64\native\onnxruntime")) {
  if (-not $iss.Contains($runtimeFile)) {
    throw "Production installer is missing required DeepCW runtime payload: $runtimeFile"
  }
}

$licenseReadme = Get-Content (Require-File "licenses\README.txt") -Raw
if (-not $licenseReadme.Contains("DeepCW model") -or
    -not $licenseReadme.Contains("HamNoise research backend")) {
  throw "Third-party license index is missing CW runtime/research notices."
}

Write-Host "CW production release validation passed."
Write-Host "DeepCW revision: $expectedRevision"
Write-Host "CW TX capability default: enabled (explicit Arm TX still required)"
Write-Host "HamNoise production packaging: absent"
Write-Host "Recorded corpus schema: v1"
