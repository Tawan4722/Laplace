#define AppName "Laplace"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppPublisher "Laplace Project"
#define AppURL "https://github.com/Tawan4722/Laplace"
#define AppExeName "laplace.exe"
#define AppId "{{F0EF4E86-D377-46E9-983A-50A83D5E6E52}"

#ifndef PublishDir
  #define PublishDir "..\\artifacts\\publish\\win-x64"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\Laplace
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts\installer
OutputBaseFilename=LaplaceSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Additional icons:"
Name: "shellintegration"; Description: "Enable .lpc association and Explorer context menu"; GroupDescription: "Laplace integration:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*"; DestDir: "{app}\docs"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autodesktop}\Laplace"; Filename: "{app}\{#AppExeName}"; Parameters: "--help"; Tasks: desktopicon
Name: "{autoprograms}\Laplace"; Filename: "{app}\{#AppExeName}"; Parameters: "--help"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "integrate install --cli-path ""{app}\{#AppExeName}"""; Flags: runhidden waituntilterminated; Tasks: shellintegration

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "integrate uninstall"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Laplace\cache"
