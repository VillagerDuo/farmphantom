#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PublishDir
  #error PublishDir must point to the self-contained publish directory.
#endif

#ifndef RuntimeIdentifier
  #define RuntimeIdentifier "win-x64"
#endif

#define AppName "FarmPhantom"

[Setup]
AppId={{4B171D84-66C1-4628-9688-E0B51FE1747A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=EmparorFQ
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=.
OutputBaseFilename={#AppName}-Setup-{#RuntimeIdentifier}
SetupIconFile=..\app\FarmPhantom.ico
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\FarmPhantom.exe
UninstallDisplayName={#AppName}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\FarmPhantom.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\FarmPhantom.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FarmPhantom.exe"; Description: "Launch {#AppName}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\steam_api64.dll"
