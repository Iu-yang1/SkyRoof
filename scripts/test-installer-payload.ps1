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

$ports = Join-Path $target 'System.IO.Ports.dll'
$version = [System.Reflection.AssemblyName]::GetAssemblyName($ports).Version
if ($version.Major -ne 10) {
  throw "Installed System.IO.Ports.dll major version is $($version.Major), expected 10."
}
Write-Host ("Installer payload OK: {0} DLLs, System.IO.Ports assembly {1}" -f
  $expectedDlls.Count, $version)
