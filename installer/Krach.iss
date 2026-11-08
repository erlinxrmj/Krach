; Installer für Krach Soundboard – entwickelt von Erlin Ramaj
#ifndef AppVer
  #define AppVer "1.0.0"
#endif

[Setup]
AppId={{6F2C9B1E-4A7D-4C1B-9E3F-1A2B3C4D5E6F}
AppName=Krach Soundboard
AppVersion={#AppVer}
AppPublisher=Erlin Ramaj
DefaultDirName={localappdata}\Programs\Krach
DefaultGroupName=Krach Soundboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\out
OutputBaseFilename=Krach-Setup
SetupIconFile=..\src\Krach\Assets\krach.ico
UninstallDisplayIcon={app}\Krach.exe
UninstallDisplayName=Krach Soundboard
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Verknüpfung auf dem Desktop erstellen"; GroupDescription: "Verknüpfungen:"

[Files]
Source: "..\publish\Krach.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\ANLEITUNG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Krach Soundboard"; Filename: "{app}\Krach.exe"
Name: "{group}\Anleitung"; Filename: "{app}\ANLEITUNG.md"
Name: "{autodesktop}\Krach Soundboard"; Filename: "{app}\Krach.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Krach.exe"; Description: "Krach jetzt starten"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Krach Soundboard"" /F"; Flags: runhidden; RunOnceId: "KrachTask"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Krach Soundboard"; Flags: uninsdeletevalue dontcreatekey
