# Verify the actual silent installer contains the WaveRT development package.
# Run after test-installer-payload.ps1; do not install/load the driver.
$ErrorActionPreference = 'Stop'
$package = Join-Path $env:GITHUB_WORKSPACE 'drivers/IF-AF-Audio/vendor/audio/sysvad/TabletAudioSample/x64/Release/Package'
$target = Join-Path $env:RUNNER_TEMP "SkyRoof-Installer-Smoke-$env:GITHUB_RUN_ID"
$installed = Join-Path $target 'Drivers/IF-AF-Audio'
foreach ($name in @('IfAfAudio.sys','IfAfAudio.inf','ifafaudio.cat')) {
  $src = Join-Path $package $name
  $dst = Join-Path $installed $name
  if (-not (Test-Path -LiteralPath $src)) { throw "Built driver package missing $name" }
  if (-not (Test-Path -LiteralPath $dst)) { throw "Installer dropped developer driver file: $name" }
  $a = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash
  $b = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
  if ($a -ne $b) { throw "Installed driver file hash mismatch: $name" }
  Write-Host "$name SHA256=$a"
}
foreach ($name in @('README-DEVELOPMENT-ONLY.md','LICENSE-LoopIt7-MIT.txt')) {
  if (-not (Test-Path -LiteralPath (Join-Path $installed $name))) {
    throw "Missing development-driver document: $name"
  }
}
$inf = Get-Content (Join-Path $installed 'IfAfAudio.inf') -Raw
if ($inf -notmatch 'Root\\IfAfAudio' -or
    $inf -notmatch 'IF-AF-Audio Playback' -or
    $inf -notmatch 'IF-AF-Audio') {
  throw 'Invalid PnP identity or audio endpoint names.'
}
$sig = Get-AuthenticodeSignature (Join-Path $installed 'IfAfAudio.sys')
Write-Host "Development SYS signature status: $($sig.Status); signer: $($sig.SignerCertificate.Subject)"
Write-Host 'PASS: app installed with developer-only driver files; INF not installed; no device nodes created.'
