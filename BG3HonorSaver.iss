; BG3 Honor Saver - Inno Setup Script
; Download Inno Setup: https://jrsoftware.org/isdl.php

#define MyAppName "Chairface's BG3 Honor Saver"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CrunchyFlix Software"
#define MyAppExeName "CBG3BackupManager.exe"
#define MyAppURL "https://github.com/Chairface30/ChairfacesBG3HonorSaver"

[Setup]
; App identifiers
AppId={{6DF6E9B7-6CEE-4DBC-B26E-1AE1C43F555D}}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no


; Installation directory
DefaultDirName={autopf}\{#MyAppPublisher}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; Output
OutputDir=.
OutputBaseFilename=CBG3HonorSaver-Setup
Compression=lzma2
SolidCompression=yes

; Modern look
WizardStyle=modern
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\icon.ico

; Privileges
PrivilegesRequired=admin

; Version info
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Installer
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; All files from the publish directory
Source: "bin\Release\net10.0-windows\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Start Menu shortcut
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"
; Desktop shortcut (only if user checks the box)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\icon.ico"; Tasks: desktopicon

[Run]
; Launch app after install (checkbox is checked by default)
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Optional: Check for .NET 10 and offer to download
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
  DotNetInstalled: Boolean;
begin
  Result := True;
  
  // Check if .NET 10 is installed
  DotNetInstalled := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost') or
                     RegKeyExists(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost');
  
  if not DotNetInstalled then
  begin
    if MsgBox('.NET 10 Desktop Runtime is required but not installed.' + #13#10 + #13#10 + 
              'Would you like to download and install it now?' + #13#10 + #13#10 + 
              '(The installer will open the Microsoft download page)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOW, ewNoWait, ResultCode);
      Result := False; // Stop installation
      MsgBox('Please install .NET 10 Desktop Runtime, then run this installer again.', mbInformation, MB_OK);
    end
    else
    begin
      Result := False; // User declined
    end;
  end;
end;
