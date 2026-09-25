<#
.SYNOPSIS
  Stop and remove the Nexus B1 Budget Windows service. Run from an ELEVATED PowerShell.
  The published files and the data folder are left in place.

.PARAMETER Name
  Windows service name. Default: NexusB1Budget
#>
param(
  [string]$Name = "NexusB1Budget"
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
if (-not $svc) { Write-Host "Service '$Name' is not installed." -ForegroundColor Yellow; return }

if ($svc.Status -ne 'Stopped') {
  Write-Host "Stopping '$Name'..." -ForegroundColor Cyan
  Stop-Service -Name $Name -Force
}
sc.exe delete $Name | Out-Null
Write-Host "Removed service '$Name'. Published files and data were left untouched." -ForegroundColor Green
