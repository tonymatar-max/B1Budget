<#
.SYNOPSIS
  Install (or reinstall) Cobalt B1 Budget as a Windows service. Run from an ELEVATED PowerShell.

.PARAMETER Dest
  The published folder produced by publish.ps1. Default: C:\CobaltB1Budget

.PARAMETER Name
  Windows service name. Default: CobaltB1Budget

.PARAMETER Account
  Service log-on account. Default: LocalSystem. For a domain/local account pass e.g. ".\svc_budget"
  together with -Password.

.PARAMETER Password
  Password for -Account (omit for LocalSystem / a gMSA).

.PARAMETER DataFrom
  Optional source data folder (with budget.db and keys\) to seed into $Dest\data before starting -
  e.g. to ship demo data. Existing data is backed up first.

.EXAMPLE
  ./deploy/install-service.ps1 -Dest C:\CobaltB1Budget
  ./deploy/install-service.ps1 -Dest C:\CobaltB1Budget -DataFrom "C:\Claude\b1-budget\server\Data"
#>
param(
  [string]$Dest    = "C:\CobaltB1Budget",
  [string]$Name    = "CobaltB1Budget",
  [string]$Display = "Cobalt B1 Budget",
  [string]$Account = "LocalSystem",
  [string]$Password,
  [string]$DataFrom
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
  Description    = "Cobalt B1 Budget API and web UI (http://localhost:5140)."
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

# Optionally seed the data folder (e.g. demo data) before the first start.
if ($DataFrom) {
  $srcDb = Join-Path $DataFrom "budget.db"
  if (-not (Test-Path $srcDb)) { throw "-DataFrom '$DataFrom' has no budget.db." }
  $dataDir = Join-Path $Dest "data"
  New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
  if (Test-Path (Join-Path $dataDir "budget.db")) {
    $backup = "$dataDir.backup-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
    Copy-Item $dataDir $backup -Recurse
    Write-Host "Backed up existing data to $backup" -ForegroundColor DarkGray
  }
  Remove-Item (Join-Path $dataDir "budget.db-wal"),(Join-Path $dataDir "budget.db-shm") -Force -ErrorAction SilentlyContinue
  Copy-Item $srcDb (Join-Path $dataDir "budget.db") -Force
  if (Test-Path (Join-Path $DataFrom "keys")) { Copy-Item (Join-Path $DataFrom "keys") $dataDir -Recurse -Force }
  Write-Host "Seeded data from $DataFrom" -ForegroundColor DarkGray
}

Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $Name
Start-Sleep -Seconds 3
Get-Service -Name $Name | Format-Table -AutoSize

Write-Host "`nInstalled. Open http://localhost:5140 on this server to create the first administrator." -ForegroundColor Green
