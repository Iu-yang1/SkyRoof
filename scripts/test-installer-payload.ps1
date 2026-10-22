# Validate the ACTUAL installed Inno payload, not just the build output.
# The application can compile while a hand-maintained [Files] list silently
# excludes a new PackageReference (e.g. System.IO.Ports.dll).
$ErrorActionPreference = 'Stop'

$build = (Resolve-Path 'SkyRoof/bin/x64/Release/net10.0-windows7.0').Path
$setup = (Resolve-Path 'install/Output/SkyRoofSetup.exe').Path
$target = Join-Path $env:RUNNER_TEMP "SkyRoof-Installer-Smoke-$env:GITHUB_RUN_ID"

$mandatory = @('SkyRoof.exe', 'SkyRoof.dll', 'SkyRoof.deps.json',
  'SkyRoof.runtimeconfig.json', 'System.IO.Ports.dll')
foreach ($name in $mandatory) {
  if (-not (Test-Path -LiteralPath (Join-Path $build $name))) {
    throw "Build output missing required runtime file: $name"
  }
}

if (Test-Path -LiteralPath $target) {
  Remove-Item -LiteralPath $target -Recurse -Force
}
New-Item -Path $target -ItemType Directory -Force | Out-Null

$arguments = @(
  '/VERYSILENT'
  '/SUPPRESSMSGBOXES'
  '/NORESTART'
  '/SP-'
  "/DIR=`"$target`""
)
Write-Host "Installing smoke build to $target"
$p = Start-Process -FilePath $setup -ArgumentList $arguments -PassThru -Wait
if ($p.ExitCode -ne 0) {
  throw "SkyRoofSetup.exe failed with exit code $($p.ExitCode)"
}

# Every top-level application .dll must survive packaging and installation.
$expectedDlls = @(Get-ChildItem -LiteralPath $build -Filter '*.dll' -File |
  Select-Object -ExpandProperty Name)
$actualDlls = @(Get-ChildItem -LiteralPath $target -Filter '*.dll' -File |
  Select-Object -ExpandProperty Name)
$missingDlls = @($expectedDlls | Where-Object { $_ -notin $actualDlls })
if ($missingDlls.Count -gt 0) {
  throw "Installer omitted DLLs: $($missingDlls -join ', ')"
}
foreach ($name in $mandatory) {
  if (-not (Test-Path -LiteralPath (Join-Path $target $name))) {
    throw "Installer omitted required application file: $name"
  }
}

# The .NET host resolves RID-specific NuGet runtimeTargets by relative
# path in SkyRoof.deps.json. Checking only {app}\\System.IO.Ports.dll
# previously produced a false positive and a real startup crash.
$depsPath = Join-Path $target 'SkyRoof.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json -AsHashtable
$windowsAssets = @()
foreach ($framework in $deps.targets.Values) {
  foreach ($package in $framework.Values) {
    if (-not $package.ContainsKey('runtimeTargets')) { continue }
    foreach ($asset in $package.runtimeTargets.GetEnumerator()) {
      if ($asset.Value.rid -notin @('win','win-x64')) { continue }
      $assetName = $asset.Key.Replace('/', [IO.Path]::DirectorySeparatorChar)
      if (Test-Path -LiteralPath (Join-Path $build $assetName)) {
        $windowsAssets += $assetName
      }
    }
  }
}
foreach ($asset in $windowsAssets | Select-Object -Unique) {
  if (-not (Test-Path -LiteralPath (Join-Path $target $asset))) {
    throw "Installer omitted .deps.json Windows runtime asset: $asset"
  }
}

$ports = Join-Path $target 'runtimes\\win\\lib\\net10.0\\System.IO.Ports.dll'
if (-not (Test-Path -LiteralPath $ports)) {
  throw "Installer missing RID-specific System.IO.Ports.dll. Root DLL alone is insufficient."
}
$version = [System.Reflection.AssemblyName]::GetAssemblyName($ports).Version
if ($version.ToString() -ne '10.0.0.0') {
  throw "Installed Windows System.IO.Ports.dll version is $version, expected 10.0.0.0."
}
Write-Host ("Installer payload OK: {0} root DLLs, {1} RID runtime assets, System.IO.Ports {2}" -f
  $expectedDlls.Count, @($windowsAssets | Select-Object -Unique).Count, $version)
