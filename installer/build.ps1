<#
.SYNOPSIS
  Builds the RizzServer MSI installer (self-contained, win-x64).
.EXAMPLE
  ./installer/build.ps1                 # version from Directory.Build.props
  ./installer/build.ps1 -Version 1.2.0
  # Equivalent without the script:
  dotnet publish RizzServer.App -p:PublishProfile=Msi
#>
param(
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}

$publish = Join-Path $root 'artifacts\publish'
$out = Join-Path $root 'artifacts\installer'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "Publishing RizzServer $Version (self-contained, win-x64)..."
dotnet publish (Join-Path $root 'RizzServer.App') -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host "Building MSI..."
dotnet build (Join-Path $root 'installer\RizzServer.Installer') -c $Configuration `
    -p:Version=$Version -p:PublishDir="$publish\" -o $out
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }

$msi = Get-ChildItem $out -Filter *.msi | Select-Object -First 1
Write-Host ("Done: {0} ({1:N1} MB)" -f $msi.FullName, ($msi.Length / 1MB))
