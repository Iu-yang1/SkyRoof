# Smoke test Inno's ACTUAL extracted files after test-installer-payload.ps1.
# The signed original VB-Audio ZIP is installed as a DATA file, never unpacked
# and no kernel PnP driver is installed by CI's /VERYSILENT smoke test.
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:RUNNER_TEMP "SkyRoof-Installer-Smoke-$env:GITHUB_RUN_ID"
$bundled = Join-Path $target 'ThirdParty\VB-CABLE'
$vendor = Join-Path $env:GITHUB_WORKSPACE 'Vendor\VB-CABLE'
foreach ($name in @('VBCABLE_Driver_Pack45.zip','VBCABLE_Driver_Pack45.sha256')) {
  $from = Join-Path $vendor $name
  $to = Join-Path $bundled $name
  if (-not (Test-Path -LiteralPath $from)) { throw "Missing downloaded official original $name" }
  if (-not (Test-Path -LiteralPath $to)) { throw "Installer omitted $name" }
  if ((Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash -ne
      (Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash) {
    throw "Installer modified official VB-CABLE archive or manifest: $name"
  }
}
foreach ($name in @('Manage-VbCable.ps1','VB-CABLE-NOTICE.txt')) {
  if (-not (Test-Path -LiteralPath (Join-Path $bundled $name))) {
    throw "Installer omitted VB-CABLE attribution/management: $name"
  }
}
if (Test-Path (Join-Path $target 'Drivers\IF-AF-Audio')) {
  throw 'Experimental self-built WaveRT driver is forbidden in the new installer.'
}
& (Join-Path $bundled 'Manage-VbCable.ps1') -Action Verify
if ($LASTEXITCODE -ne 0) { throw 'Bundled official zip failed code signing verification.' }
Write-Host 'PASS: unmodified official VB-CABLE basic ZIP, notice and management shipped with SkyRoof.'
