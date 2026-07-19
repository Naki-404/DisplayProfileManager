# Build compact single-file app + Windows installer
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$publish = Join-Path $root 'dist\app'
$payload = Join-Path $root 'Installer\payload'
$outSetup = Join-Path $root 'dist'

Write-Host '== Publish app (single-file, win-x64, framework-dependent) ==' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish .\DisplayProfileManager.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

# Drop junk that may still appear
Get-ChildItem $publish -Include '*.pdb','*.xml' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# QRes beside the app (tiny fallback for resolution)
$qres = 'C:\Tools\QRes\QRes.exe'
if (Test-Path $qres) {
    Copy-Item $qres (Join-Path $publish 'QRes.exe') -Force
}

Write-Host '== Stage installer payload (app files only) ==' -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payload | Out-Null
# Strict whitelist — never ship screenshots/docs/extra junk
$appExe = Join-Path $publish 'DisplayProfileManager.exe'
if (-not (Test-Path $appExe)) { throw "Missing $appExe" }
Copy-Item $appExe (Join-Path $payload 'DisplayProfileManager.exe') -Force
$qresOut = Join-Path $publish 'QRes.exe'
if (Test-Path $qresOut) {
  Copy-Item $qresOut (Join-Path $payload 'QRes.exe') -Force
}
Get-ChildItem $payload | ForEach-Object { '  payload: {0}' -f $_.Name }

Write-Host '== Build Setup.exe ==' -ForegroundColor Cyan
$setupOut = Join-Path $outSetup 'setup-build'
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
dotnet publish .\Installer\Installer.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $setupOut
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }

$final = Join-Path $outSetup 'DisplayProfileManager-Setup.exe'
Copy-Item (Join-Path $setupOut 'DisplayProfileManager-Setup.exe') $final -Force

# Also keep a portable single-folder copy
$portable = Join-Path $outSetup 'portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item (Join-Path $publish '*') $portable -Force

# Clear Mark-of-the-Web on local artifacts (SmartScreen is quieter without Zone.Identifier)
Get-ChildItem $outSetup -Recurse -Include '*.exe' -ErrorAction SilentlyContinue | ForEach-Object {
  try { Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue } catch {}
  $zone = $_.FullName + ':Zone.Identifier'
  if (Test-Path $zone) { Remove-Item $zone -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Get-ChildItem $publish | ForEach-Object { '  app: {0,8:N1} KB  {1}' -f ($_.Length/1KB), $_.Name }
Get-ChildItem $final | ForEach-Object { '  setup:{0,8:N1} KB  {1}' -f ($_.Length/1KB), $_.Name }
Write-Host ''
Write-Host "Installer: $final"
Write-Host "Portable:  $portable"
Write-Host ''
Write-Host 'Note: SmartScreen "Unknown publisher" is fully removed only with a paid Authenticode certificate.' -ForegroundColor DarkYellow
Write-Host 'Author/publisher metadata is set to Nakidev. Local builds are Unblock-File cleaned.'
