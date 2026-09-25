<#
.SYNOPSIS
  Install (or reinstall) Nexus B1 Budget as a Windows service. Run from an ELEVATED PowerShell.

.PARAMETER Dest
  The published folder produced by publish.ps1. Default: C:\NexusB1Budget

.PARAMETER Name
  Windows service name. Default: NexusB1Budget

.PARAMETER Account
  Service log-on account. Default: LocalSystem. For a domain/local account pass e.g. ".\svc_budget"
  together with -Password.

.PARAMETER Password
  Password for -Account (omit for LocalSystem / a gMSA).

.EXAMPLE
  ./deploy/install-service.ps1 -Dest C:\NexusB1Budget
#>
param(
  [string]$Dest    = "C:\NexusB1Budget",
  [string]$Name    = "NexusB1Budget",
  [string]$Display = "Nexus B1 Budget",
  [string]$Account = "LocalSystem",
  [string]$Password
)
$ErrorActionPreference = "Stop"

# Must be elevated: creating a service and an event-log source both need admin.
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$exe = Join-Path $Dest "B1Budget.Api.exe"
if (-not (Test-Path $exe)) { throw "$exe not found. Run deploy\publish.ps1 -Dest '$Dest' first." }

# Event-log source used by Program.cs (builder.Logging.AddEventLog). Create it as admin so the service
# account (which may not be able to create it) can write to it.
if (-not [System.Diagnostics.EventLog]::SourceExists($Display)) {
  New-EventLog -LogName Application -Source $Display
  Write-Host "Created event-log source '$Display'." -ForegroundColor DarkGray
}

# Remove an existing service so this script is a clean reinstall.
$existing = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($existing) {
  Write-Host "Stopping and removing existing service '$Name'..." -ForegroundColor Yellow
  if ($existing.Status -ne 'Stopped') { Stop-Service -Name $Name -Force }
  sc.exe delete $Name | Out-Null
  Start-Sleep -Seconds 2
}

Write-Host "Creating service '$Name'..." -ForegroundColor Cyan
$params = @{
  Name           = $Name
  BinaryPathName = "`"$exe`""
  DisplayName    = $Display
  Description    = "Nexus B1 Budget API and web UI (http://localhost:5140)."
  StartupType    = "Automatic"
}
if ($Account -ne "LocalSystem") {
  if (-not $Password) { throw "Provide -Password for account '$Account' (or use the default LocalSystem)." }
  $sec = ConvertTo-SecureString $Password -AsPlainText -Force
  $params.Credential = New-Object System.Management.Automation.PSCredential($Account, $sec)
}
New-Service @params | Out-Null

# Restart automatically if it ever crashes.
sc.exe failure $Name reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $Name
Start-Sleep -Seconds 3
Get-Service -Name $Name | Format-Table -AutoSize

Write-Host "`nInstalled. Open http://localhost:5140 on this server to create the first administrator." -ForegroundColor Green
