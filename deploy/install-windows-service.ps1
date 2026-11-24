<#
.SYNOPSIS
  Installs the headless RizzServer as a Windows service (run from an elevated PowerShell).
.EXAMPLE
  ./deploy/install-windows-service.ps1 -Path C:\RizzServer -DataDir C:\ProgramData\RizzServer
  ./deploy/install-windows-service.ps1 -Uninstall
#>
param(
    [string]$Path = 'C:\Program Files\RizzServer Service',
    [string]$DataDir = 'C:\ProgramData\RizzServer',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$name = 'RizzServer'

if ($Uninstall) {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
        Write-Host "Service removed. Data in $DataDir is untouched."
    }
    return
}

$exe = Join-Path $Path 'rizzserver-service.exe'
if (-not (Test-Path $exe)) { throw "rizzserver-service.exe not found in $Path (publish it first: installer/publish-portable.ps1 -Component Service)" }

New-Item -ItemType Directory -Force $DataDir | Out-Null

# A service runs as LocalSystem by default, whose profile folder is not where you'd look for settings,
# so point it at an explicit data directory.
sc.exe create $name binPath= "`"$exe`"" start= auto DisplayName= "RizzServer OPC UA gateway" | Out-Null
sc.exe description $name "OPC UA gateway for Modbus, RS-232, MQTT, OPC DA and OPC UA sources" | Out-Null
sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
New-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name Environment -PropertyType MultiString `
    -Value @("RIZZSERVER_DATA_DIR=$DataDir") -Force | Out-Null

Start-Service $name
Write-Host "Installed and started. REST API: http://127.0.0.1:5080  OPC UA: opc.tcp://<host>:4840/RizzServer"
$env:RIZZSERVER_DATA_DIR = $DataDir   # so --show-api-key reads the service's settings, not yours
Write-Host "API key:" (& $exe --show-api-key)
