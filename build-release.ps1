<#
.SYNOPSIS
  Builds the Visor release into .\release :
    Visor-Setup.exe              per-user installer (.NET Framework 4.8 host + embedded app payload)
    Visor-<version>-portable.zip Visor.exe + Assets (self-contained, no .NET runtime needed)
    SHA256SUMS.txt

.PARAMETER Force
  Stop Visor / Visor-Setup processes that run from this repo's build folders (they lock outputs).
  Without -Force the script only reports them.

.NOTES
  Requires the .NET 10 SDK. Version comes from Directory.Build.props (<Version>).
#>
[CmdletBinding()]
param(
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version 2.0

$root = $PSScriptRoot
$release = Join-Path $root 'release'
$publishDir = Join-Path $release '_publish'
$uninstDir = Join-Path $release '_uninstaller'
$setupDir = Join-Path $release '_setup'
$stageDir = Join-Path $root '_payload-stage'
$payloadZip = Join-Path $root 'Installer\payload.zip'
$appProject = Join-Path $root 'Visor.csproj'
$setupProject = Join-Path $root 'Installer\Installer.csproj'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Write-Step([string]$text) { Write-Host "== $text ==" -ForegroundColor Cyan }

function Invoke-Dotnet {
  param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$What)
  & dotnet @Arguments
  if ($LASTEXITCODE -ne 0) { throw "$What failed (dotnet exit code $LASTEXITCODE)" }
}

function Remove-Dir([string]$path) {
  if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}

# Zip a folder with forward-slash entry names (Compress-Archive on PS 5.1 writes backslashes).
function New-Zip([string]$sourceDir, [string]$zipPath, [string]$prefix = '') {
  if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
  $base = (Resolve-Path -LiteralPath $sourceDir).Path.TrimEnd('\') + '\'
  $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
  try {
    Get-ChildItem -LiteralPath $sourceDir -Recurse -File | Sort-Object FullName | ForEach-Object {
      $name = $prefix + $_.FullName.Substring($base.Length).Replace('\', '/')
      [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $zip, $_.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
  } finally { $zip.Dispose() }
}

# --- version (single source: Directory.Build.props) ---
[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
$versionNode = $props.SelectSingleNode('//Version')
if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
  throw 'No <Version> in Directory.Build.props'
}
$version = $versionNode.InnerText.Trim()
Write-Host "Visor $version" -ForegroundColor Green

# --- processes that would lock build outputs ---
$lockers = @(Get-Process -Name 'Visor', 'Visor-Setup' -ErrorAction SilentlyContinue | Where-Object {
    $p = $null
    try { $p = $_.Path } catch { }
    $p -and $p.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
  })
if ($lockers.Count -gt 0) {
  $list = ($lockers | ForEach-Object { "  pid $($_.Id): $($_.Path)" }) -join "`n"
  if ($Force) {
    Write-Warning "Stopping processes running from the repo:`n$list"
    $lockers | Stop-Process -Force
    Start-Sleep -Milliseconds 500
  } else {
    Write-Warning "These processes run from the repo and may lock build outputs (re-run with -Force to stop them):`n$list"
  }
}

# --- clean ---
Write-Step 'Clean'
New-Item -ItemType Directory -Force -Path $release | Out-Null
foreach ($d in @($publishDir, $uninstDir, $setupDir, $stageDir, (Join-Path $release 'portable'))) { Remove-Dir $d }
if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force }
# Previous outputs of THIS script only - never files it didn't produce (e.g. a kept legacy build).
Get-ChildItem -LiteralPath $release -File -ErrorAction SilentlyContinue | Where-Object {
  $_.Name -like 'Visor-*-portable.zip' -or $_.Name -eq 'Visor-Setup.exe' -or $_.Name -eq 'SHA256SUMS.txt'
} | ForEach-Object {
  try { Remove-Item -LiteralPath $_.FullName -Force } catch { Write-Warning "Could not delete $($_.Name): $($_.Exception.Message)" }
}

try {
  # --- 1. app: self-contained, single-file, compressed ---
  Write-Step 'Publish Visor.exe (self-contained single-file, compressed)'
  Invoke-Dotnet -What 'App publish' -Arguments @(
    'publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-o', $publishDir, '-nologo')

  $appExe = Join-Path $publishDir 'Visor.exe'
  if (-not (Test-Path -LiteralPath $appExe)) { throw "Publish produced no $appExe" }

  # Payload = Visor.exe + Assets\Sounds + Assets\Packs (copied by the Content items in Visor.csproj).
  New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
  Copy-Item -LiteralPath $appExe -Destination $stageDir
  foreach ($sub in @('Sounds', 'Packs')) {
    $src = Join-Path $publishDir "Assets\$sub"
    if (-not (Test-Path -LiteralPath $src)) {
      Write-Warning "Publish output has no Assets\$sub - copying from the source tree"
      $src = Join-Path $root "Assets\$sub"
    }
    if (Test-Path -LiteralPath $src) {
      New-Item -ItemType Directory -Force -Path (Join-Path $stageDir 'Assets') | Out-Null
      Copy-Item -LiteralPath $src -Destination (Join-Path $stageDir "Assets\$sub") -Recurse
    }
  }
  $extra = @(Get-ChildItem -LiteralPath $publishDir -File | Where-Object { $_.Name -ne 'Visor.exe' })
  if ($extra.Count -gt 0) {
    Write-Warning ("Publish produced extra files that are NOT shipped: " + (($extra | ForEach-Object Name) -join ', '))
  }

  # --- 2. portable zip (before Uninstall.exe joins the stage) ---
  Write-Step 'Portable zip'
  $portableZip = Join-Path $release "Visor-$version-portable.zip"
  New-Zip -sourceDir $stageDir -zipPath $portableZip -prefix 'Visor/'

  # --- 3. uninstaller: the installer built WITHOUT payload, shipped as Uninstall.exe ---
  Write-Step 'Build Uninstall.exe (installer without payload)'
  Invoke-Dotnet -What 'Uninstaller build' -Arguments @(
    'build', $setupProject, '-c', 'Release', '--no-incremental', '-o', $uninstDir, '-nologo')
  Copy-Item -LiteralPath (Join-Path $uninstDir 'Visor-Setup.exe') -Destination (Join-Path $stageDir 'Uninstall.exe')

  # --- 4. payload.zip + Setup ---
  Write-Step 'Build Visor-Setup.exe (with payload)'
  New-Zip -sourceDir $stageDir -zipPath $payloadZip
  Invoke-Dotnet -What 'Setup build' -Arguments @(
    'build', $setupProject, '-c', 'Release', '--no-incremental', '-o', $setupDir, '-nologo')
  $setupExe = Join-Path $release 'Visor-Setup.exe'
  Copy-Item -LiteralPath (Join-Path $setupDir 'Visor-Setup.exe') -Destination $setupExe -Force

  $setupExtra = @(Get-ChildItem -LiteralPath $setupDir -File | Where-Object { $_.Name -ne 'Visor-Setup.exe' })
  if ($setupExtra.Count -gt 0) {
    Write-Warning ("Setup build produced extra files (not needed at runtime): " + (($setupExtra | ForEach-Object Name) -join ', '))
  }
}
finally {
  foreach ($d in @($publishDir, $uninstDir, $setupDir, $stageDir)) {
    try { Remove-Dir $d } catch { Write-Warning "Could not remove ${d}: $($_.Exception.Message)" }
  }
  if (Test-Path -LiteralPath $payloadZip) { Remove-Item -LiteralPath $payloadZip -Force -ErrorAction SilentlyContinue }
}

# --- summary ---
$artifacts = @(
  (Get-Item -LiteralPath (Join-Path $release 'Visor-Setup.exe')),
  (Get-Item -LiteralPath (Join-Path $release "Visor-$version-portable.zip"))
)
$sums = foreach ($a in $artifacts) {
  $hash = (Get-FileHash -LiteralPath $a.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  [pscustomobject]@{ Name = $a.Name; SizeMB = [math]::Round($a.Length / 1MB, 2); SHA256 = $hash }
}
($sums | ForEach-Object { "$($_.SHA256)  $($_.Name)" }) -join "`n" |
  Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding Ascii

Write-Host ''
Write-Host "Done. Visor $version artifacts in $release" -ForegroundColor Green
$sums | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
