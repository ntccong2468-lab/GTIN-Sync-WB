#define AppVersion "0.3.0"
[Setup]
AppId={{1A71EAD8-AB04-46A4-9F9B-5149EE426D62}
AppName=GTIN Sync WB
AppVersion={#AppVersion}
DefaultDirName={autopf}\GTIN Sync WB
DefaultGroupName=GTIN Sync WB
OutputDir=..\dist
OutputBaseFilename=GTIN-Sync-WB-Setup-{#AppVersion}-win-x64
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\src\GTINSyncWB\app.ico
UninstallDisplayIcon={app}\GTINSyncWB.exe
WizardStyle=modern
[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
[Icons]
Name: "{autoprograms}\GTIN Sync WB"; Filename: "{app}\GTINSyncWB.exe"
Name: "{autodesktop}\GTIN Sync WB"; Filename: "{app}\GTINSyncWB.exe"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên desktop"; Flags: checkedonce
[Run]
Filename: "{app}\GTINSyncWB.exe"; Description: "Mở GTIN Sync WB"; Flags: nowait postinstall skipifsilent
