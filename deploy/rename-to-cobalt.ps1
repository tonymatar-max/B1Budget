<#
.SYNOPSIS
  One-time migration: replace the old "NexusB1Budget" Windows service at C:\NexusB1Budget with the renamed
  "CobaltB1Budget" service at C:\CobaltB1Budget, carrying the database and DataProtection keys across.
  Run from an ELEVATED PowerShell, after publishing the new build to -Staging (deploy\publish.ps1 -Dest <staging>).

.PARAMETER Staging
  Folder holding the freshly published new build. Default: C:\CobaltB1Budget-staging

.PARAMETER OldName / OldDest   The existing service and its folder. Defaults: NexusB1Budget, C:\NexusB1Budget
.PARAMETER NewName / NewDest   The renamed service and its folder. Defaults: CobaltB1Budget, C:\CobaltB1Budget

.NOTES
  The saved SAP B1 Service Layer password must be re-entered afterwards: the forecast/budget data moves intact,
  but DataProtection now keys on the pinned application name instead of the old folder path, so the encrypted
  password cannot be read until you re-enter it once under Companies.
#>
param(
  [string]$Staging = "C:\CobaltB1Budget-staging",
  [string]$OldName = "NexusB1Budget",
  [string]$OldDest = "C:\NexusB1Budget",
  [string]$NewName = "CobaltB1Budget",
  [string]$NewDest = "C:\CobaltB1Budget",
  [string]$Display = "Cobalt B1 Budget"
)
$ErrorActionPreference = "Stop"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this script from an elevated (Administrator) PowerShell." }
if (-not (Test-Path (Join-Path $Staging "B1Budget.Api.exe"))) { throw "No build in '$Staging'. Run deploy\publish.ps1 -Dest '$Staging' first." }

# 1. Stop and remove the old service.
$old = Get-Service -Name $OldName -ErrorAction SilentlyContinue
if ($old) {
  Write-Host "Stopping and removing old service '$OldName'..." -ForegroundColor Yellow
  if ($old.Status -ne 'Stopped') { Stop-Service -Name $OldName -Force; (Get-Service $OldName).WaitForStatus('Stopped','00:00:30') }
  sc.exe delete $OldName | Out-Null
  Start-Sleep -Seconds 2
}

# 2. Lay down the new build at the new location.
New-Item -ItemType Directory -Force -Path $NewDest | Out-Null
Write-Host "Copying new build to $NewDest ..." -ForegroundColor Cyan
robocopy $Staging $NewDest /E /XD (Join-Path $NewDest "data") | Out-Null

# 3. Bring the data (budget.db + keys) across - move the old folder's data if the new one has none yet.
$newData = Join-Path $NewDest "data"; $oldData = Join-Path $OldDest "data"
if (-not (Test-Path (Join-Path $newData "budget.db")) -and (Test-Path (Join-Path $oldData "budget.db"))) {
  Write-Host "Moving data from $oldData ..." -ForegroundColor Cyan
  New-Item -ItemType Directory -Force -Path $newData | Out-Null
  robocopy $oldData $newData /E /MOVE | Out-Null
}

# 4. Event-log source for the renamed app.
if (-not [System.Diagnostics.EventLog]::SourceExists($Display)) { New-EventLog -LogName Application -Source $Display }

# 5. Install and start the renamed service.
Write-Host "Creating service '$NewName'..." -ForegroundColor Cyan
$bin = '"' + (Join-Path $NewDest 'B1Budget.Api.exe') + '"'
New-Service -Name $NewName -BinaryPathName $bin -DisplayName $Display -Description "Cobalt B1 Budget API and web UI (http://localhost:5140)." -StartupType Automatic | Out-Null
sc.exe failure $NewName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service -Name $NewName
Start-Sleep -Seconds 3
Get-Service $NewName | Format-Table -AutoSize

Write-Host "`nDone. Open http://localhost:5140 (hard-refresh)." -ForegroundColor Green
Write-Host "Re-enter each SAP B1 company's Service Layer password under Companies - the move reset the saved secret." -ForegroundColor Yellow
Write-Host "Once verified, you can delete the old folder: $OldDest" -ForegroundColor DarkGray
