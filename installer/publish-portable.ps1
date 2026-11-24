<#
.SYNOPSIS
  Publishes self-contained RizzServer builds (desktop App or headless Service) for Windows, Linux and macOS and archives them.
.EXAMPLE
  ./installer/publish-portable.ps1
  ./installer/publish-portable.ps1 -Runtimes linux-x64
  ./installer/publish-portable.ps1 -Component Service -Runtimes linux-x64
#>
param(
    [ValidateSet('App', 'Service')]
    [string]$Component = 'App',
    [string[]]$Runtimes = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
}

$out = Join-Path $root 'artifacts\portable'
New-Item -ItemType Directory -Force $out | Out-Null

foreach ($rid in $Runtimes) {
    $prefix = if ($Component -eq 'Service') { 'RizzServer.Service' } else { 'RizzServer' }
    $dir = Join-Path $out "$prefix-$Version-$rid"
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }

    Write-Host "Publishing $rid..."
    dotnet publish (Join-Path $root "RizzServer.$Component") -c $Configuration -r $rid --self-contained true `
        -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false -o $dir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    if ($rid -like 'win-*') {
        $archive = "$dir.zip"
        if (Test-Path $archive) { Remove-Item $archive }
        Compress-Archive -Path "$dir\*" -DestinationPath $archive
    }
    else {
        # tar keeps the Unix executable bit that zip from Windows would lose.
        $archive = "$dir.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive }
        tar -czf $archive -C $dir .
    }
    Remove-Item $dir -Recurse -Force
    Write-Host ("  {0} ({1:N1} MB)" -f $archive, ((Get-Item $archive).Length / 1MB))
}
