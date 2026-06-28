# install.ps1
# This script must be run as Administrator in PowerShell.

$ErrorActionPreference = "Stop"

Write-Host "=============================================" -ForegroundColor Cyan
Write-Host " Installing LockScreenDemo Windows Service " -ForegroundColor Cyan
Write-Host "=============================================" -ForegroundColor Cyan

# 1. Self-elevation check
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script MUST be run as an Administrator. Please restart PowerShell as Administrator."
    Exit
}

$InstallDir = "C:\ProgramData\LockScreenDemo"
$BinDir = "$InstallDir\bin"

# Set working directory to the script directory to ensure relative paths work correctly
Set-Location $PSScriptRoot


# Stop existing service if running
if (Get-Service -Name "LockScreenDemoService" -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing LockScreenDemoService..." -ForegroundColor Yellow
    sc.exe stop LockScreenDemoService | Out-Null
    Start-Sleep -Seconds 2
    Write-Host "Deleting existing LockScreenDemoService..." -ForegroundColor Yellow
    sc.exe delete LockScreenDemoService | Out-Null
    Start-Sleep -Seconds 1
}

# Kill any running Service, Agent, or Viewer processes to free files
Write-Host "Terminating existing Service, Agent, and Viewer processes..." -ForegroundColor Yellow
Get-Process -Name "LockScreenDemo.Service" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "LockScreenDemo.Agent" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "LockScreenDemo.Viewer" -ErrorAction SilentlyContinue | Stop-Process -Force

# Clean up any existing logs or screenshot files to avoid permission conflicts
Remove-Item -Path "$InstallDir\lockscreen.png", "$InstallDir\agent_log.txt" -Force -ErrorAction SilentlyContinue

# Create target directories
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

# Configure ACL to allow Everyone to read/write/modify. This prevents Access Denied errors
# when the Agent runs standalone under a standard user account and tries to modify logs/images
# that were originally created by the SYSTEM service account.
try {
    $acl = Get-Acl $InstallDir
    $ar = New-Object System.Security.AccessControl.FileSystemAccessRule("Everyone", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $acl.SetAccessRule($ar)
    Set-Acl $InstallDir $acl
} catch {
    Write-Host "Warning: Failed to set directory permissions: $_" -ForegroundColor Yellow
}

if (-not (Test-Path $BinDir)) {
    New-Item -ItemType Directory -Path $BinDir | Out-Null
}

# 2. Compile and Publish the solution, or copy existing binaries if source is not present
if (Test-Path "LockScreenDemo.slnx") {
    Write-Host "Source solution found. Compiling and publishing projects..." -ForegroundColor Cyan
    dotnet publish LockScreenDemo.Service\LockScreenDemo.Service.csproj -c Release -o "$BinDir" --self-contained false
    dotnet publish LockScreenDemo.Agent\LockScreenDemo.Agent.csproj -c Release -o "$BinDir" --self-contained false
    dotnet publish LockScreenDemo.Viewer\LockScreenDemo.Viewer.csproj -c Release -o "$BinDir" --self-contained false
} else {
    Write-Host "Source solution not found. Installing from pre-compiled binaries..." -ForegroundColor Cyan
    Get-ChildItem -Path $PSScriptRoot -Exclude "*.ps1" | Copy-Item -Destination $BinDir -Recurse -Force
}

# Copy the installation scripts to the bin directory so the Viewer can find them for future install/uninstall actions
Copy-Item -Path (Join-Path $PSScriptRoot "install.ps1") -Destination $BinDir -Force
Copy-Item -Path (Join-Path $PSScriptRoot "uninstall.ps1") -Destination $BinDir -Force

# Verify output files exist
if (-not (Test-Path "$BinDir\LockScreenDemo.Service.exe")) {
    Write-Error "Required service binary LockScreenDemo.Service.exe not found in $BinDir."
    Exit
}

# 3. Create Windows Service
Write-Host "Registering LockScreenDemoService..." -ForegroundColor Cyan
$ServicePath = "$BinDir\LockScreenDemo.Service.exe"
# We wrap path in quotes to handle spaces correctly
sc.exe create LockScreenDemoService binPath= "`"$ServicePath`"" start= auto | Out-Null
sc.exe description LockScreenDemoService "Windows Lock Screen Access Proof-of-Concept Service" | Out-Null

# 4. Start Windows Service
Write-Host "Starting LockScreenDemoService..." -ForegroundColor Cyan
sc.exe start LockScreenDemoService | Out-Null

# 5. Configure Windows Firewall
if (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue) {
    Write-Host "Configuring Windows Firewall to allow TCP port 5800..." -ForegroundColor Cyan
    Remove-NetFirewallRule -Name "LockScreenDemo" -ErrorAction SilentlyContinue | Out-Null
    New-NetFirewallRule -Name "LockScreenDemo" -DisplayName "LockScreenDemo" -Direction Inbound -LocalPort 5800 -Protocol TCP -Action Allow -ErrorAction SilentlyContinue | Out-Null
}

Write-Host ""
Write-Host "Installation Complete!" -ForegroundColor Green
Write-Host "The service is now running and monitoring user sessions." -ForegroundColor Green
Write-Host "Check output files and screenshot at: $InstallDir" -ForegroundColor Green
Write-Host ""
Write-Host "Launching WPF Viewer..." -ForegroundColor Cyan

# Spawn WPF Viewer
Start-Process "$BinDir\LockScreenDemo.Viewer.exe"
