# uninstall.ps1
# This script must be run as Administrator in PowerShell.

$ErrorActionPreference = "Continue"

Write-Host "=============================================" -ForegroundColor Red
Write-Host " Uninstalling LockScreenDemo Windows Service " -ForegroundColor Red
Write-Host "=============================================" -ForegroundColor Red

# Self-elevation check
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script MUST be run as an Administrator. Please restart PowerShell as Administrator."
    Exit
}

$InstallDir = "C:\ProgramData\LockScreenDemo"
$BinDir = "$InstallDir\bin"

# Set working directory to script directory
Set-Location $PSScriptRoot

# 1. Stop and Delete Service
if (Get-Service -Name "LockScreenDemoService" -ErrorAction SilentlyContinue) {
    Write-Host "Stopping LockScreenDemoService..." -ForegroundColor Yellow
    sc.exe stop LockScreenDemoService | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Deleting LockScreenDemoService..." -ForegroundColor Yellow
    sc.exe delete LockScreenDemoService | Out-Null
    Start-Sleep -Seconds 1
}

# 2. Kill running processes
Write-Host "Terminating active processes..." -ForegroundColor Yellow
Get-Process -Name "LockScreenDemo.Agent" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "LockScreenDemo.Viewer" -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. Clean up installation files
Write-Host "Cleaning up installation directory..." -ForegroundColor Yellow
if (Test-Path $BinDir) {
    Remove-Item -Recurse -Force $BinDir
}
if (Test-Path $InstallDir) {
    # Delete everything inside but keep folder itself in case it is open
    Remove-Item -Path "$InstallDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path $InstallDir -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Uninstallation Complete!" -ForegroundColor Green
