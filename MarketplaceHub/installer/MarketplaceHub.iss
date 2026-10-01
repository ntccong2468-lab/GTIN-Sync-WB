#define MyAppName "Marketplace Hub"
#define MyAppVersion "0.7.4"
#define MyAppPublisher "ntccong2468-lab"
#define MyAppExeName "MarketplaceHub.exe"
[Setup]
AppId={{F097A459-52DF-4DBB-8B66-4E6E7AD43591}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\MarketplaceHub
DefaultGroupName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=MarketplaceHub-Setup-{#MyAppVersion}-win-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
[Files]
Source: "..\bin\Release\net8.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Open Marketplace Hub"; Flags: nowait postinstall skipifsilent
