param(
  [string]$OutputDirectory = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$revision = "1af3a77b2ff18dada2149f36686430cdae7cf13c"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
  $OutputDirectory = Join-Path $repoRoot "Vendor\HamNoise\x64"
}

$runnerTemp = $env:RUNNER_TEMP
if ([string]::IsNullOrWhiteSpace($runnerTemp)) {
  $runnerTemp = $env:TEMP
}
$workRoot = Join-Path $runnerTemp "skyroof-hamnoise-$revision"
$sourceDir = Join-Path $workRoot "source"
$objDir = Join-Path $workRoot "obj"

if (Test-Path $workRoot) {
  Remove-Item $workRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
New-Item -ItemType Directory -Path $objDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Push-Location $sourceDir
try {
  git init --quiet
  git remote add origin https://github.com/e04/HamNoise.git
  git fetch --quiet --depth 1 origin $revision
  git checkout --quiet --detach FETCH_HEAD

  $actual = (git rev-parse HEAD).Trim()
  if ($actual -ne $revision) {
    throw "HamNoise revision mismatch: expected $revision, got $actual"
  }
}
finally {
  Pop-Location
}

$pf86 = [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")
$vswhere = Join-Path $pf86 "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
  throw "vswhere.exe not found; Visual Studio Build Tools are required."
}

$vs = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if ([string]::IsNullOrWhiteSpace($vs)) {
  throw "Visual C++ x64 build tools were not found."
}

$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) {
  throw "vcvars64.bat not found at $vcvars"
}

$bridge = Join-Path $repoRoot "native\HamNoiseBridge\hamnoise_bridge.cpp"
$coreInclude = Join-Path $sourceDir "core\include"
$coreGenerated = Join-Path $sourceDir "core\generated"
$coreAudio = Join-Path $sourceDir "core\src\denoise_audio.c"
$coreGru = Join-Path $sourceDir "core\src\denoise_gru.c"
$v2Dir = Join-Path $sourceDir "web\wasm\v2"
$v2Generated = Join-Path $v2Dir "generated"
$v2Engine = Join-Path $v2Dir "voice_v2_engine.cpp"

$bridgeObj = Join-Path $objDir "hamnoise_bridge.obj"
$audioObj = Join-Path $objDir "denoise_audio.obj"
$gruObj = Join-Path $objDir "denoise_gru.obj"
$v2Obj = Join-Path $objDir "cw_v2_engine.obj"
$outDll = Join-Path $OutputDirectory "hamnoise_skyroof.dll"
$outPdb = Join-Path $OutputDirectory "hamnoise_skyroof.pdb"

function Q([string]$value) {
  return '"' + $value.Replace('"', '""') + '"'
}

$commonCpp =
  '/nologo /O2 /MD /EHsc /std:c++20 ' +
  '/D DENOISE_WEB_TARGET_CW=1 /D DENOISE_WEB_TARGET_VOICE=0 ' +
  '/I ' + (Q $coreInclude) + ' /I ' + (Q $coreGenerated) +
  ' /I ' + (Q $v2Dir) + ' /I ' + (Q $v2Generated)

$commands = @(
  ('cl ' + $commonCpp + ' /c ' + (Q $bridge) + ' /Fo:' + (Q $bridgeObj)),
  ('cl /nologo /O2 /MD /TC /I ' + (Q $coreInclude) +
   ' /c ' + (Q $coreAudio) + ' /Fo:' + (Q $audioObj)),
  ('cl /nologo /O2 /MD /TC /I ' + (Q $coreInclude) +
   ' /c ' + (Q $coreGru) + ' /Fo:' + (Q $gruObj)),
  ('cl ' + $commonCpp + ' /c ' + (Q $v2Engine) + ' /Fo:' + (Q $v2Obj)),
  ('link /nologo /DLL /MACHINE:X64 /OUT:' + (Q $outDll) +
   ' /PDB:' + (Q $outPdb) + ' ' +
   (Q $bridgeObj) + ' ' + (Q $audioObj) + ' ' + (Q $gruObj) + ' ' + (Q $v2Obj))
)

$cmdLine = 'call ' + (Q $vcvars) + ' >nul && ' + ($commands -join ' && ')
& cmd.exe /d /s /c $cmdLine
if ($LASTEXITCODE -ne 0) {
  throw "HamNoise native build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $outDll)) {
  throw "HamNoise native bridge DLL was not produced."
}

$sha = (Get-FileHash $outDll -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "HamNoise revision: $revision"
Write-Host "HamNoise bridge: $outDll"
Write-Host "HamNoise bridge SHA-256: $sha"
