; Per-user installer for Tangelo (no admin rights required).
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\Tangelo"
#endif

[Setup]
AppId={{6B1F5E0A-7C3E-4C1B-9A6D-0D7E2A1B9F42}
AppName=Tangelo
AppVersion={#AppVersion}
AppPublisher=Tangelo
AppPublisherURL=https://github.com/thisisrishi/Tangelo
AppSupportURL=https://github.com/thisisrishi/Tangelo/issues
DefaultDirName={localappdata}\Programs\Tangelo
DefaultGroupName=Tangelo
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=TangeloSetup-{#AppVersion}-x64
SetupIconFile=..\src\Tangelo.App\Assets\tangelo.ico
UninstallDisplayIcon={app}\Tangelo.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\..\LICENSE
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Launch Tangelo when I sign in"; GroupDescription: "Options:"
Name: "explorer"; Description: "Add ""Tangelo…"" to the File Explorer right-click menu"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Tangelo"; Filename: "{app}\Tangelo.exe"
Name: "{userdesktop}\Tangelo"; Filename: "{app}\Tangelo.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Tangelo"; ValueData: """{app}\Tangelo.exe"""; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\*\shell\Tangelo"; ValueType: string; ValueData: "Tangelo…"; Tasks: explorer; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\Tangelo"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Tangelo.exe"",0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\*\shell\Tangelo"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Player"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\*\shell\Tangelo\command"; ValueType: string; ValueData: """{app}\Tangelo.exe"" ""%1"""; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Tangelo"; ValueType: string; ValueData: "Tangelo…"; Tasks: explorer; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Tangelo"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\Tangelo.exe"",0"; Tasks: explorer
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Tangelo\command"; ValueType: string; ValueData: """{app}\Tangelo.exe"" ""%1"""; Tasks: explorer
; Remove entries the app may have written itself.
Root: HKCU; Subkey: "Software\Classes\*\shell\Tangelo"; Flags: uninsdeletekey dontcreatekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Tangelo"; Flags: uninsdeletekey dontcreatekey

[Run]
Filename: "{app}\Tangelo.exe"; Description: "Start Tangelo"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM Tangelo.exe /F"; Flags: runhidden; RunOnceId: "KillTangelo"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\Tangelo"
