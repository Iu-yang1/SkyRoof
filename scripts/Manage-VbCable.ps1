# SkyRoof VB-CABLE vendor installer adapter. Only ORIGINAL VB-Audio basic zip.
# License: https://vb-audio.com/Services/licensing.htm
# Do not distribute modified drivers or paid A+B/C+D packages.
[CmdletBinding()]
param(
  [ValidateSet('Verify','Install','Uninstall')][string]$Action = 'Verify',
  [string]$ArchivePath = (Join-Path $PSScriptRoot 'VBCABLE_Driver_Pack45.zip'),
  [switch]$Consent,
  [string]$LogPath = (Join-Path $PSScriptRoot 'VB-CABLE-manager.log')
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
  if ($expectVendor -and $sig.SignerCertificate.Subject -notmatch '(?i)Vincent Burel|Burel Vincent|VB-AUDIO') {
    throw "Unexpected VB-CABLE setup publisher: $($sig.SignerCertificate.Subject)"
  }
  Write-Host ("Verified: {0} ({1})" -f (Split-Path $path -Leaf), $sig.SignerCertificate.Subject)
}
# Report the precise failed phase. UAC PowerShell consoles can close before
# the operator reads their error, so actions also write a local diagnostic log.
# The log lives alongside the bundled manager, not in the user's Documents.
$stage = 'Initialization'
$result = 1
$transcriptStarted = $false
try {
  if ($Action -ne 'Verify') {
    $stage = 'Consent and elevation'
    if (-not $Consent) { throw 'Explicit user opt-in is required.' }
    Assert-Admin

    $stage = 'Diagnostic logging'
    $logDirectory = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
      New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }
    # Overwrite, rather than append, so GUI never displays yesterday's error.
    Start-Transcript -LiteralPath $LogPath -Force | Out-Null
    $transcriptStarted = $true
    Write-Host "SkyRoof VB-CABLE manager: $Action"
    Write-Host "Log: $LogPath"
  }

  $stage = 'Windows device discovery'
  $media = @(Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match '(?i)VB-Audio.*(Virtual.*Cable|Cable)' })
  $endpoints = @(Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue |
    Where-Object { $_.FriendlyName -match '(?i)CABLE\s*(Input|Output|In|Out).*VB-Audio|VB-Audio.*CABLE\s*(Input|Output|In|Out)' })
  $hasInput = @($endpoints | Where-Object {
    $_.FriendlyName -match '(?i)CABLE\s*(Input|In)' -and $_.Status -eq 'OK'
  }).Count -gt 0
  $hasOutput = @($endpoints | Where-Object {
    $_.FriendlyName -match '(?i)CABLE\s*(Output|Out)' -and $_.Status -eq 'OK'
  }).Count -gt 0
  if ($Action -ne 'Verify') {
    Write-Host ("PnP state: media={0}, readyRender={1}, readyCapture={2}" -f
      $media.Count, $hasInput, $hasOutput)
    foreach ($item in @($media) + @($endpoints)) {
      Write-Host ("  PnP: [{0}] {1} ({2})" -f $item.Class, $item.FriendlyName, $item.Status)
    }
  }

  if ($Action -eq 'Install' -and $hasInput -and $hasOutput) {
    Write-Host 'Both VB-CABLE Windows audio endpoints are ready; no driver change needed.'
    $result = 0
  }
  elseif ($Action -eq 'Install' -and ($media.Count -gt 0 -or $endpoints.Count -gt 0)) {
    # Installing over a half-registered driver is unreliable; never silently
    # remove drivers which may have been installed by another program.
    throw 'Partial VB-CABLE installation detected. Restart Windows and rescan. If still partial, use the original vendor setup to repair/remove it, then restart before retrying.'
  }
  elseif ($Action -eq 'Uninstall' -and -not (Test-Path $stateKey)) {
    throw 'SkyRoof did not install VB-CABLE. Refusing to remove an independently installed shared driver.'
  }
  else {
    $stage = 'Bundled archive and SHA256'
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
      throw "Original VB-CABLE basic package missing: $ArchivePath. Vendor: $vendorUrl"
    }
    $hash = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
    $expectedPath = Join-Path (Split-Path -Parent $ArchivePath) 'VBCABLE_Driver_Pack45.sha256'
    if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
      throw 'No pinned official download SHA256 manifest shipped with SkyRoof.'
    }
    $expected = ((Get-Content -LiteralPath $expectedPath -Raw).Trim() -split '\s+')[0]
    if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or $hash -ne $expected) {
      throw "VB-CABLE archive SHA256 mismatch. Expected $expected, got $hash"
    }

    $stage = 'Unpacking original vendor archive'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $work
    $setups = @(Get-ChildItem $work -Recurse -File -Filter 'VBCABLE_Setup_x64.exe')
    if ($setups.Count -ne 1) { throw 'Exactly one original vendor x64 setup is required.' }
    $setup = $setups[0]
    $catalogs = @(Get-ChildItem $work -Recurse -File -Filter '*.cat')
    if ($catalogs.Count -lt 1) { throw 'VB-CABLE ZIP has no Windows driver signature catalog.' }

    $stage = 'Original setup Authenticode signature'
    Check-Signature $setup.FullName $true
    $stage = 'Original driver catalog signature'
    $win10Catalog = @($catalogs | Where-Object { $_.Name -match '(?i)win10' })
    if ($win10Catalog.Count -lt 1) { throw 'Expected Windows 10/11 driver catalog missing.' }
    foreach ($catalog in $win10Catalog) {
      Check-Signature $catalog.FullName $false
    }

    if ($Action -eq 'Verify') {
      Write-Host "Original VB-CABLE package verified, SHA256 $hash"
      $result = 0
    }
    else {
      # Original vendor setup performs driver registration. Neither SkyRoof
      # nor the manager replaces, patches, self-signs, or trusts its .sys.
      $stage = "Original vendor setup ($Action)"
      $flags = if ($Action -eq 'Install') { @('-i','-h') } else { @('-u','-h') }
      Write-Host "Starting original signed VB-Audio setup: $($setup.Name) $($flags -join ' ')"
      $process = Start-Process -FilePath $setup.FullName -ArgumentList $flags `
        -WorkingDirectory $setup.DirectoryName -PassThru -Wait
      Write-Host "Vendor setup exit code: $($process.ExitCode)"
      if ($process.ExitCode -ne 0) {
        throw "Original VB-Audio setup returned $($process.ExitCode). Inspect Windows INF/setupapi.dev.log for driver installation errors; no ownership marker was created."
      }

      $stage = 'Recording SkyRoof installation ownership'
      if ($Action -eq 'Install') {
        New-Item -Path $stateKey -Force | Out-Null
        New-ItemProperty -Path $stateKey -Name Vendor -Value 'VB-Audio Software' -Force | Out-Null
        New-ItemProperty -Path $stateKey -Name InstallerArchiveSHA256 -Value $hash -Force | Out-Null
        Write-Host 'Vendor setup succeeded. Restart Windows before using CABLE Input / Output.'
      }
      else {
        Remove-Item $stateKey -Recurse -Force
        Write-Host 'Vendor setup completed removal. Restart Windows to finish.'
      }
      $result = 3010
    }
  }
}
catch {
  Write-Host ("VB-CABLE FAILED at stage [{0}]: {1}" -f $stage, $_.Exception.Message)
  $result = 1
}
finally {
  if ($transcriptStarted) {
    try { Stop-Transcript | Out-Null } catch { }
  }
  if (Test-Path -LiteralPath $work -PathType Container) {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
  }
}
exit $result
