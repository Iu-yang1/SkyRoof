# Fetch the untouched VB-Audio basic 2024 package for the experimental SkyRoof installer.
# Official donationware bundling grant: https://vb-audio.com/Services/licensing.htm
param(
  [string]$Destination = 'Vendor/VB-CABLE',
  [string]$ExpectedSha256 = ''
)
$ErrorActionPreference = 'Stop'
$url = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip'
$folder = [IO.Path]::GetFullPath($Destination)
New-Item $folder -ItemType Directory -Force | Out-Null
$archive = Join-Path $folder 'VBCABLE_Driver_Pack45.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToUpperInvariant()
Write-Host "Official VB-Audio basic package SHA256: $hash"
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256) -and $hash -ne $ExpectedSha256) {
  Remove-Item $archive -Force
  throw "Pinned official VB-CABLE archive changed; expected $ExpectedSha256 got $hash"
}
Set-Content -LiteralPath (Join-Path $folder 'VBCABLE_Driver_Pack45.sha256') -Value $hash -NoNewline -Encoding ascii
& (Join-Path $PSScriptRoot 'Manage-VbCable.ps1') -Action Verify -ArchivePath $archive
if ($LASTEXITCODE -ne 0) { throw 'Vendor setup/archive signature verification failed.' }
Write-Host 'Official unmodified VB-CABLE ZIP staged for experimental Inno installer.'
