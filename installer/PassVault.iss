; PassVault installer - Inno Setup 6
;
; Build it with:
;     dotnet publish src/PassVault.App/PassVault.App.csproj -c Release -r win-x64 ^
;         --self-contained true -o dist/app -p:PublishSingleFile=true ^
;         -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=en
;     "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\PassVault.iss
;
; The app is published SELF-CONTAINED, so .NET is inside it. There is no runtime to
; download, nothing to agree to, and nothing to go wrong on a machine with no internet.

#define AppName       "PassVault"
#define AppVersion    "1.0.0"
#define AppPublisher  "PassVault"
#define AppExe        "PassVault.exe"

[Setup]
AppId={{8F3C21D4-7B65-4E9A-9C18-2D4A6E0B51F7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Installs for the current user only, which means NO User Account Control prompt.
; A password manager asking for administrator on first run is a bad first impression,
; and nothing here needs machine-wide access - the vault lives in the user's own profile.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
; Modern style hides the welcome page by default. We want it: it is the one place to say
; up front that this needs a USB stick, before anyone gets halfway through.
DisableWelcomePage=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=PassVault-{#AppVersion}-setup
SetupIconFile=..\brand\passvault.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardImageFile=..\brand\installer-large.bmp
WizardSmallImageFile=..\brand\installer-small.bmp
WizardImageStretch=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a shortcut on the desktop"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName} now"; Flags: nowait postinstall skipifsilent

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nPassVault keeps your passwords in a file that only opens with your USB stick, this PC, and your PIN. Everything stays on your own machine - nothing is sent anywhere.%n%nYou will need a spare USB stick to finish setting up.

[Code]
{ Uninstalling must never take the vault with it.

  %LOCALAPPDATA%\PassVault holds this PC's setup and the spare copy of the encrypted
  vault - the thing that saves you when the stick dies. Deleting it on uninstall would
  quietly destroy a backup the person does not know they are relying on, so we leave it
  and tell them where it is. }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\PassVault');
    if DirExists(DataDir) then
      MsgBox('PassVault has been removed.' + #13#10 + #13#10 +
             'Your vault data has been left alone, including the spare copy of your ' +
             'encrypted vault:' + #13#10 + #13#10 +
             DataDir + #13#10 + #13#10 +
             'Delete that folder yourself if you are certain you no longer need it. ' +
             'Your USB stick and your 12 words are untouched either way.',
             mbInformation, MB_OK);
  end;
end;
