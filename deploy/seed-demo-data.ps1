<#
.SYNOPSIS
  Copy an existing budget database (and DataProtection keys) into the installed service's data
  folder, e.g. to seed the Windows service with your demo data. Run from an ELEVATED PowerShell.

.DESCRIPTION
  Stops the service, backs up whatever data it currently has, copies budget.db (+ keys) from the
  source, clears any stale SQLite WAL/SHM sidecar files, then restarts the service.

.PARAMETER From
  Source data folder that holds budget.db (and a keys\ folder). Default: the dev server's data.

.PARAMETER Dest
  The installed service's data folder. Default: C:\CobaltB1Budget\data

.PARAMETER Name
  Windows service name. Default: CobaltB1Budget

.EXAMPLE
  ./deploy/seed-demo-data.ps1
  ./deploy/seed-demo-data.ps1 -From "C:\Claude\b1-budget\server\Data" -Dest "C:\CobaltB1Budget\data"
#>
param(
  [string]$From = "C:\Claude\b1-budget\server\Data",
  [string]$Dest = "C:\CobaltB1Budget\data",
  [string]$Name = "CobaltB1Budget"
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }

$srcDb = Join-Path $From "budget.db"
if (-not (Test-Path $srcDb)) { throw "No budget.db found in '$From'." }
if (Test-Path (Join-Path $From "budget.db-wal")) {
  Write-Warning "Source has an open WAL file - the source app may be running. Close it first so the DB on disk is complete."
}

$svc = Get-Service -Name $Name -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne 'Stopped') {
  Write-Host "Stopping service '$Name'..." -ForegroundColor Cyan
  Stop-Service -Name $Name -Force
  (Get-Service $Name).WaitForStatus('Stopped','00:00:30')
}

New-Item -ItemType Directory -Force -Path $Dest | Out-Null

# Back up the current data folder before overwriting.
if (Test-Path (Join-Path $Dest "budget.db")) {
  $backup = "$Dest.backup-{0:yyyyMMdd-HHmmss}" -f (Get-Date)
  Write-Host "Backing up current data to $backup" -ForegroundColor DarkGray
  Copy-Item $Dest $backup -Recurse
}

# Remove stale sidecars in the destination so SQLite doesn't mix an old WAL with the new DB.
Remove-Item (Join-Path $Dest "budget.db-wal"),(Join-Path $Dest "budget.db-shm") -Force -ErrorAction SilentlyContinue

Write-Host "Copying budget.db from $From ..." -ForegroundColor Cyan
Copy-Item $srcDb (Join-Path $Dest "budget.db") -Force
foreach ($side in "budget.db-wal","budget.db-shm") {
  $p = Join-Path $From $side
  if (Test-Path $p) { Copy-Item $p (Join-Path $Dest $side) -Force }
}

# Copy DataProtection keys too, so cookie sessions and any encrypted SL passwords line up.
if (Test-Path (Join-Path $From "keys")) {
  Copy-Item (Join-Path $From "keys") $Dest -Recurse -Force
}

if ($svc) {
  Write-Host "Starting service '$Name'..." -ForegroundColor Cyan
  Start-Service -Name $Name
  Start-Sleep -Seconds 3
  Get-Service $Name | Format-Table -AutoSize
}
Write-Host "Done. Open http://localhost:5140 to check the data is there." -ForegroundColor Green
Write-Host "Note: SL passwords encrypted on another machine/folder may need re-entering (mock companies are fine)." -ForegroundColor Yellow
