; installer.iss
; Inno Setup Script for LockScreenDemo

[Setup]
AppName=LockScreenDemo
AppVersion=1.0
AppPublisher=Manikanda Bharathi
DefaultDirName={autopf}\LockScreenDemo
DefaultGroupName=LockScreenDemo
UninstallDisplayIcon={app}\LockScreenDemo.Viewer.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.\Output
OutputBaseFilename=LockScreenDemoSetup
; Enable native 64-bit installation mode for Program Files
ArchitecturesInstallIn64BitMode=x64
; Administrator privileges are required to register the Windows Service
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Files]
; Source all published release files
Source: ".\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Add Desktop and Start Menu Shortcuts for the Viewer app
Name: "{group}\LockScreenDemo Viewer"; Filename: "{app}\LockScreenDemo.Viewer.exe"
Name: "{commondesktop}\LockScreenDemo Viewer"; Filename: "{app}\LockScreenDemo.Viewer.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Run]
; 1. Register the Windows Service using sc.exe
Filename: "sc.exe"; Parameters: "create LockScreenDemoService binPath= ""\""{app}\LockScreenDemo.Service.exe\"""" start= auto"; Flags: runhidden; Description: "Registering Windows Service"; StatusMsg: "Installing background service..."

; 2. Add Service Description
Filename: "sc.exe"; Parameters: "description LockScreenDemoService ""Windows Lock Screen Session Agent Host Service"""; Flags: runhidden

; 3. Add Firewall Rule
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""LockScreenDemo"" dir=in action=allow protocol=TCP localport=5800"; Flags: runhidden

; 4. Start the Service
Filename: "sc.exe"; Parameters: "start LockScreenDemoService"; Flags: runhidden; Description: "Starting Windows Service"; StatusMsg: "Starting background service..."

; 5. Optionally launch the WPF Viewer after setup completes
Filename: "{app}\LockScreenDemo.Viewer.exe"; Description: "Launch LockScreenDemo Viewer"; Flags: postinstall nowait skipifsilent

[UninstallRun]
; 1. Stop and Delete the Windows Service on uninstallation
Filename: "sc.exe"; Parameters: "stop LockScreenDemoService"; Flags: runhidden; RunOnceId: "StopService"
Filename: "sc.exe"; Parameters: "delete LockScreenDemoService"; Flags: runhidden; RunOnceId: "DeleteService"

; 2. Clean up firewall rule
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""LockScreenDemo"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

; 3. Clean up any leftover agent or viewer processes
Filename: "taskkill.exe"; Parameters: "/f /im LockScreenDemo.Agent.exe"; Flags: runhidden; RunOnceId: "KillAgent"
Filename: "taskkill.exe"; Parameters: "/f /im LockScreenDemo.Viewer.exe"; Flags: runhidden; RunOnceId: "KillViewer"

[Code]
// Terminate running processes and services before file copying starts to prevent file locks (Error 5)
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Stop and delete the service first if it exists
  Exec('sc.exe', 'stop LockScreenDemoService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete LockScreenDemoService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  // Kill running agent/viewer/service processes
  Exec('taskkill.exe', '/f /im LockScreenDemo.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im LockScreenDemo.Agent.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im LockScreenDemo.Viewer.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  Result := True;
end;

// Custom code to clean up log/screenshot directories generated in ProgramData during app run
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Clean up ProgramData directory if empty or delete logs/screenshots
    DelTree(ExpandConstant('{commonappdata}\LockScreenDemo'), True, True, True);
  end;
end;
