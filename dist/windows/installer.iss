; Gnosis VPN for Windows: one-file installer (Inno Setup 6). Built by .github/workflows/windows-installer.yml:
;   iscc /DAppVersion=0.2.2 /DPayload=<folder with Install.cmd, install.ps1, app\, service\> /DOutDir=<dir> installer.iss
; The installer does not copy the app or the service itself: it unpacks the same folder the zip
; package has into a temporary folder and runs install.ps1 from there, so the zip and the installer
; install exactly the same way. It keeps uninstall.ps1 for "Apps > Installed apps > Uninstall".

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Payload
  #error Payload (the package folder) is required
#endif
#ifndef OutDir
  #define OutDir "."
#endif

[Setup]
AppId={{6F0C2A34-9E5B-4D1C-8B7A-0D9E3C5B2A61}
AppName=Gnosis VPN
AppVersion={#AppVersion}
AppPublisher=Gnosis VPN for Windows (prototype, for testing only)
AppPublisherURL=https://github.com/SCBuergel/gvpndows
; Only the uninstaller and its scripts live here; install.ps1 owns this folder's permissions.
DefaultDirName={commonpf64}\Gnosis VPN Service
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutDir}
OutputBaseFilename=GnosisVPN-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Gnosis VPN
UninstallDisplayIcon={commonpf64}\Gnosis VPN\GnosisVPN.exe
; The app's icon everywhere (winui/tools/gen_icons.py derives the images from it)
SetupIconFile=..\..\winui\GnosisVpn\Assets\app.ico
WizardImageFile=wizard-164.bmp,wizard-328.bmp
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-110.bmp
; shown before installing (skipped by /SILENT and /VERYSILENT)
InfoBeforeFile=DISCLAIMER.txt
CloseApplications=no
SetupLogging=yes

[Files]
; the package, unpacked next to the installer's own temporary files (removed when setup ends)
Source: "{#Payload}\*"; DestDir: "{tmp}\GnosisVPN"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#Payload}\uninstall.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{#Payload}\common.ps1"; DestDir: "{app}\setup"; Flags: ignoreversion

[Run]
Filename: "{commonpf64}\Gnosis VPN\GnosisVPN.exe"; Description: "Start Gnosis VPN"; Flags: postinstall nowait runasoriginaluser skipifsilent; Check: InstallOk

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c ""set ""PSModulePath="" && ""{sys}\WindowsPowerShell\v1.0\powershell.exe"" -NoProfile -ExecutionPolicy Bypass -File ""{app}\setup\uninstall.ps1"""""; Flags: runhidden waituntilterminated; RunOnceId: "GnosisVPNUninstall"

[Code]
// install.ps1 does the work (service, app, permissions, start); its output goes to a log under
// the uninstaller's folder (setup\install.log). A failure shows that log's path, skips "Start Gnosis VPN" and makes a
// silent install (/VERYSILENT) exit with code 1.
var
  InstallFailed: Boolean;

function InstallOk: Boolean;
begin
  Result := not InstallFailed;
end;

function GetCustomSetupExitCode: Integer;
begin
  if InstallFailed then Result := 1 else Result := 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Log, Script, Cmd: String;
begin
  if CurStep = ssPostInstall then
  begin
    Log := ExpandConstant('{app}\setup\install.log');
    Script := ExpandConstant('{tmp}\GnosisVPN\install.ps1');
    ForceDirectories(ExtractFileDir(Log));
    WizardForm.StatusLabel.Caption := 'Installing the Gnosis VPN service and app...';
    // -File, not -Command: a path inside a command string breaks on a user name with an apostrophe.
    // An error in install.ps1 ends powershell with exit code 1; cmd.exe passes it on.
    // PSModulePath is cleared: started from PowerShell 7, Windows PowerShell would inherit its
    // module path and fail to load its own modules (Get-Acl).
    Cmd := '/c "set "PSModulePath=" && "' + ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe') + '" -NoProfile -ExecutionPolicy Bypass -File "' + Script + '" -NoLaunch > "' + Log + '" 2>&1"';
    if not Exec(ExpandConstant('{cmd}'), Cmd,
        ExpandConstant('{tmp}\GnosisVPN'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      InstallFailed := True;
      SuppressibleMsgBox('Installing Gnosis VPN failed (exit code ' + IntToStr(ResultCode) + '). Every step is listed in ' + Log + '.', mbCriticalError, MB_OK, IDOK);
    end;
  end;
end;
