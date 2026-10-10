param(
  [Parameter(Mandatory = $true)]
  [string]$CorpusRoot,

  [string]$OutputPath = "",

  [string]$ModelDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$corpus = (Resolve-Path $CorpusRoot).Path
$manifest = Join-Path $corpus "manifest.json"
if (-not (Test-Path $manifest)) {
  throw "manifest.json was not found at corpus root '$corpus'."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  $OutputPath = Join-Path $repoRoot "artifacts\cw-recorded-corpus.json"
}

if ([string]::IsNullOrWhiteSpace($ModelDirectory)) {
  $ModelDirectory = Join-Path $repoRoot "artifacts\deepcw-model"
}
New-Item -ItemType Directory -Path $ModelDirectory -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

$revision = "8e264d243bbd4467bd19f3f28292219405b47e0e"
$model = Join-Path $ModelDirectory "model.onnx"
$metadata = Join-Path $ModelDirectory "model.onnx.json"

if (-not (Test-Path $model) -or -not (Test-Path $metadata)) {
  $base = "https://raw.githubusercontent.com/e04/deepcw-engine/$revision"
  Write-Host "Downloading pinned DeepCW revision $revision"
  Invoke-WebRequest -Uri "$base/model.onnx" -OutFile $model
  Invoke-WebRequest -Uri "$base/model.onnx.json" -OutFile $metadata
}

if ((Get-Item $model).Length -ne 15139839) {
  throw "Pinned DeepCW model size mismatch."
}
Get-Content $metadata -Raw | ConvertFrom-Json | Out-Null

$env:SKYROOF_RUN_CW_RECORDED_CORPUS = "1"
$env:CW_CORPUS_ROOT = $corpus
$env:CW_CORPUS_OUTPUT = [IO.Path]::GetFullPath($OutputPath)
$env:DEEPCW_MODEL_PATH = [IO.Path]::GetFullPath($model)
$env:DEEPCW_METADATA_PATH = [IO.Path]::GetFullPath($metadata)

Push-Location $repoRoot
try {
  dotnet test VE3NEA.Dsp.Tests/VE3NEA.Dsp.Tests.csproj -c Release -p:Platform=x64 --filter "FullyQualifiedName~CwRecordedCorpusBenchmarkTests.RecordedCorpus_EndToEnd" --logger "console;verbosity=normal"
  if ($LASTEXITCODE -ne 0) {
    throw "Recorded CW corpus benchmark failed with exit code $LASTEXITCODE."
  }
}
finally {
  Pop-Location
}

if (-not (Test-Path $env:CW_CORPUS_OUTPUT)) {
  throw "Recorded CW corpus report was not produced."
}

Write-Host ""
Write-Host "CW corpus report: $env:CW_CORPUS_OUTPUT"
Get-Content $env:CW_CORPUS_OUTPUT
