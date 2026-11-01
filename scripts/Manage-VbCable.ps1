# SkyRoof VB-CABLE vendor installer adapter. Only ORIGINAL VB-Audio basic zip.
# License: https://vb-audio.com/Services/licensing.htm
# Do not distribute modified drivers or paid A+B/C+D packages.
[CmdletBinding()]
param(
  [ValidateSet('Verify','Install','Uninstall')][string]$Action = 'Verify',
  [string]$ArchivePath = (Join-Path $PSScriptRoot 'VBCABLE_Driver_Pack45.zip'),
  [switch]$Consent
)
$ErrorActionPreference = 'Stop'
$vendorUrl = 'https://vb-audio.com/Cable/'
$stateKey = 'HKLM:\Software\SkyRoof\VbCableBundled'
$work = Join-Path ([IO.Path]::GetTempPath()) ('SkyRoof-VBCable-' + [guid]::NewGuid().ToString('N'))
function Assert-Admin {
  $id = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = [Security.Principal.WindowsPrincipal]::new($id)
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'VB-CABLE installation/removal requires administrator permission.'
  }
}
function Get-EndpointNames {
  @(
    Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue |
      Where-Object { $_.FriendlyName -match '(?i)CABLE\s*(Input|Output|In|Out).*VB-Audio|VB-Audio.*CABLE\s*(Input|Output|In|Out)' } |
      Select-Object -ExpandProperty FriendlyName
  )
}
function Test-VbDriverPresent {
  $media = @(Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match '(?i)VB-Audio.*(Virtual.*Cable|Cable)' })
  return ($media.Count -gt 0 -or @(Get-EndpointNames).Count -gt 0)
}
function Check-Signature([string]$path, [bool]$expectVendor) {
  $sig = Get-AuthenticodeSignature -LiteralPath $path
  if ($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate) {
    throw "VB-CABLE package component has invalid Windows signature: $path ($($sig.Status))"
  }
  if ($expectVendor -and $sig.SignerCertificate.Subject -notmatch '(?i)Vincent Burel|VB-AUDIO') {
    throw "Unexpected VB-CABLE setup publisher: $($sig.SignerCertificate.Subject)"
  }
  Write-Host ("Verified: {0} ({1})" -f (Split-Path $path -Leaf), $sig.SignerCertificate.Subject)
}
if ($Action -ne 'Verify') {
  if (-not $Consent) { throw 'Explicit user opt-in is required.' }
  Assert-Admin
}
if ($Action -eq 'Install' -and (Test-VbDriverPresent)) {
  Write-Host 'VB-CABLE Windows audio driver already exists; skip installation.'
  exit 0
}
if ($Action -eq 'Uninstall' -and -not (Test-Path $stateKey)) {
  Write-Host 'SkyRoof did not install VB-CABLE. Refusing to remove independently installed shared drivers.'
  exit 2
}
if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
  throw "Original VB-CABLE basic package missing: $ArchivePath. Vendor: $vendorUrl"
}
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
  $hash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
  $expectedPath = Join-Path (Split-Path -Parent $ArchivePath) 'VBCABLE_Driver_Pack45.sha256'
  if (-not (Test-Path -LiteralPath $expectedPath)) {
    throw 'No pinned official download SHA256 manifest shipped with SkyRoof.'
  }
  $expected = ((Get-Content -LiteralPath $expectedPath -Raw).Trim() -split '\s+')[0]
  if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or $hash -ne $expected) {
    throw "VB-CABLE archive SHA256 mismatch. Expected $expected, got $hash"
  }
  Expand-Archive -LiteralPath $ArchivePath -DestinationPath $work
  $setups = @(Get-ChildItem $work -Recurse -File -Filter 'VBCABLE_Setup_x64.exe')
  if ($setups.Count -ne 1) { throw 'Exactly one original vendor x64 setup is required.' }
  $setup = $setups[0]
  $catalogs = @(Get-ChildItem $work -Recurse -File -Filter '*.cat')
  if ($catalogs.Count -lt 1) { throw 'VB-CABLE ZIP has no Windows driver signature catalog.' }
  Check-Signature $setup.FullName $true
  foreach ($catalog in $catalogs) {
    if ($catalog.Name -match '(?i)win10') { Check-Signature $catalog.FullName $false }
  }
  if ($Action -eq 'Verify') {
    Write-Host "Original VB-CABLE package verified, SHA256 $hash"
    exit 0
  }
  # Original vendor setup CLI -i -h / -u -h is used by existing OSS projects.
  # The vendor setup, not SkyRoof, performs the PnP driver changes.
  $flags = if ($Action -eq 'Install') { @('-i','-h') } else { @('-u','-h') }
  $process = Start-Process -FilePath $setup.FullName -ArgumentList $flags -WorkingDirectory $setup.DirectoryName -PassThru -Wait
  if ($process.ExitCode -ne 0) {
    throw "Vendor setup failed with exit code $($process.ExitCode)."
  }
  if ($Action -eq 'Install') {
    if (-not (Test-VbDriverPresent)) {
      Write-Warning 'Driver not yet visible. Windows restart required; endpoint readiness remains unverified.'
    }
    New-Item -Path $stateKey -Force | Out-Null
    New-ItemProperty -Path $stateKey -Name Vendor -Value 'VB-Audio Software' -Force | Out-Null
    New-ItemProperty -Path $stateKey -Name InstallerArchiveSHA256 -Value $hash -Force | Out-Null
    Write-Host 'Official setup returned success. Restart Windows, then verify both audio endpoints.'
    exit 3010
  } else {
    Remove-Item $stateKey -Recurse -Force
    Write-Host 'Official setup removed VB-CABLE. Restart Windows to finish.'
    exit 3010
  }
} finally {
  Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
