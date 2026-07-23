# Build compact app + Windows installer into project-root \release
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$release = Join-Path $root 'release'
$publish = Join-Path $release '_publish'
$payloadDir = Join-Path $root 'Installer\_payload-stage'
$payloadZip = Join-Path $root 'Installer\payload.zip'
$setupPublish = Join-Path $release '_setup-publish'

Write-Host '== Clean previous release artifacts ==' -ForegroundColor Cyan
foreach ($p in @($publish, $payloadDir, $setupPublish)) {
  if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
New-Item -ItemType Directory -Force -Path $release | Out-Null

Get-Process DisplayProfileManager, 'DisplayProfileManager-Setup' -ErrorAction SilentlyContinue |
  Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Write-Host '== Publish app (single-file, AV-friendlier flags) ==' -ForegroundColor Cyan
dotnet publish .\DisplayProfileManager.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=false `
  -p:PublishReadyToRun=false `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -p:IncludeAllContentForSelfExtract=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

Get-ChildItem $publish -Include '*.pdb','*.xml' -Recurse -ErrorAction SilentlyContinue |
  Remove-Item -Force -ErrorAction SilentlyContinue

$qres = 'C:\Tools\QRes\QRes.exe'
if (Test-Path $qres) {
  Copy-Item $qres (Join-Path $publish 'QRes.exe') -Force
}

Write-Host '== Stage installer payload.zip ==' -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
$appExe = Join-Path $publish 'DisplayProfileManager.exe'
if (-not (Test-Path $appExe)) { throw "Missing $appExe" }
Copy-Item $appExe (Join-Path $payloadDir 'DisplayProfileManager.exe') -Force

$qresOut = Join-Path $publish 'QRes.exe'
if (Test-Path $qresOut) {
  Copy-Item $qresOut (Join-Path $payloadDir 'QRes.exe') -Force
}

$assetsDst = Join-Path $payloadDir 'Assets'
New-Item -ItemType Directory -Force -Path $assetsDst | Out-Null
$soundsSrc = Join-Path $root 'Assets\Sounds'
$packsSrc = Join-Path $root 'Assets\Packs'
if (Test-Path $soundsSrc) { Copy-Item $soundsSrc (Join-Path $assetsDst 'Sounds') -Recurse -Force }
if (Test-Path $packsSrc) { Copy-Item $packsSrc (Join-Path $assetsDst 'Packs') -Recurse -Force }

# Also copy Assets that landed beside publish output
$pubAssets = Join-Path $publish 'Assets'
if (Test-Path $pubAssets) {
  Copy-Item (Join-Path $pubAssets '*') $assetsDst -Recurse -Force -ErrorAction SilentlyContinue
}

Compress-Archive -Path (Join-Path $payloadDir '*') -DestinationPath $payloadZip -Force
Remove-Item $payloadDir -Recurse -Force
Write-Host ("  payload.zip: {0:N1} KB" -f ((Get-Item $payloadZip).Length / 1KB))

Write-Host '== Build Setup.exe ==' -ForegroundColor Cyan
dotnet publish .\Installer\Installer.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=false `
  -p:PublishReadyToRun=false `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $setupPublish
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }

$finalApp = Join-Path $release 'DisplayProfileManager.exe'
$finalSetup = Join-Path $release 'DisplayProfileManager-Setup.exe'
Copy-Item $appExe $finalApp -Force
Copy-Item (Join-Path $setupPublish 'DisplayProfileManager-Setup.exe') $finalSetup -Force

$portable = Join-Path $release 'portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item (Join-Path $publish '*') $portable -Recurse -Force
# Ensure portable has Assets
if (-not (Test-Path (Join-Path $portable 'Assets\Sounds')) -and (Test-Path $soundsSrc)) {
  New-Item -ItemType Directory -Force -Path (Join-Path $portable 'Assets') | Out-Null
  Copy-Item $soundsSrc (Join-Path $portable 'Assets\Sounds') -Recurse -Force
  if (Test-Path $packsSrc) { Copy-Item $packsSrc (Join-Path $portable 'Assets\Packs') -Recurse -Force }
}

Get-ChildItem $release -Recurse -Include '*.exe','*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
  try { Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
  $zone = $_.FullName + ':Zone.Identifier'
  if (Test-Path $zone) { Remove-Item $zone -Force -ErrorAction SilentlyContinue }
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupPublish -Recurse -Force -ErrorAction SilentlyContinue
# Keep payload.zip out of repo noise after embed
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Done. Artifacts in project-root \release :' -ForegroundColor Green
Get-ChildItem $release -File | ForEach-Object {
  '  {0,10:N1} KB  {1}' -f ($_.Length / 1KB), $_.Name
}
if (Test-Path $portable) {
  Write-Host ("  portable\  ({0} files)" -f (Get-ChildItem $portable -Recurse -File | Measure-Object).Count)
}
Write-Host ''
Write-Host "App:   $finalApp"
Write-Host "Setup: $finalSetup"
Write-Host ''
Write-Host 'AV: unsigned process/gamma tools often trip heuristics. This build drops ReadyToRun,' -ForegroundColor DarkYellow
Write-Host 'native self-extract DLLs, installer NvAPIWrapper, and hidden cmd cleanups.'
Write-Host 'Complete SmartScreen silence still needs a paid Authenticode certificate.'
