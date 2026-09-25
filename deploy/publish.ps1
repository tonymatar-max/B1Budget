<#
.SYNOPSIS
  Build the UI and publish the API to a self-contained folder ready to run as a Windows service.

.DESCRIPTION
  1. Builds the React client (into server/wwwroot).
  2. Publishes the .NET API for win-x64 into the target folder.
  The published folder is self-contained where possible and carries appsettings.json
  (which binds http://localhost:5140) and the built UI.

.PARAMETER Dest
  Where to publish. Default: C:\NexusB1Budget

.EXAMPLE
  ./deploy/publish.ps1 -Dest C:\NexusB1Budget
#>
param(
  [string]$Dest = "C:\NexusB1Budget"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (parent of deploy\)

Write-Host "Building UI..." -ForegroundColor Cyan
Push-Location (Join-Path $root "client")
try {
  npm install
  npm run build
} finally { Pop-Location }

Write-Host "Publishing API to $Dest ..." -ForegroundColor Cyan
Push-Location (Join-Path $root "server")
try {
  dotnet publish -c Release -r win-x64 --self-contained false -o $Dest
} finally { Pop-Location }

Write-Host "`nPublished to $Dest" -ForegroundColor Green
Write-Host "Next: run deploy\install-service.ps1 from an elevated PowerShell." -ForegroundColor Yellow
