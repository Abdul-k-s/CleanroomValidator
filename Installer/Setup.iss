; CleanroomValidator Installer
; Inno Setup Script - Single EXE Installer

#define MyAppName "CleanroomValidator"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "AUS Engineering"
#define MyAppURL "https://github.com/aus-engineering/cleanroom-validator"
#define RevitVersion "2025"

[Setup]
AppId={{8D4E2F1A-3B5C-4D6E-9F8A-1B2C3D4E5F6A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={userappdata}\Autodesk\Revit\Addins\{#RevitVersion}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
OutputDir=Output
OutputBaseFilename=CleanroomValidator_v{#MyAppVersion}_Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName} for Revit {#RevitVersion}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Main DLL and dependencies
Source: "Files\CleanroomValidator.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "Files\CleanroomValidator.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "Files\CleanroomValidator.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion

; Addin manifest goes to parent folder (2025)
Source: "Files\CleanroomValidator.addin"; DestDir: "{userappdata}\Autodesk\Revit\Addins\{#RevitVersion}"; Flags: ignoreversion

[Icons]
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Installation complete
    Log('CleanroomValidator installed successfully');
  end;
end;

[Messages]
WelcomeLabel1=Welcome to CleanroomValidator Setup
WelcomeLabel2=This will install CleanroomValidator v{#MyAppVersion} for Revit {#RevitVersion}.%n%nCleanroom compliance validation tools for GMP and ISO standards.%n%nClick Next to continue.
FinishedHeadingLabel=Installation Complete
FinishedLabel=CleanroomValidator has been installed.%n%nPlease restart Revit to load the add-in.%n%nYou will find it in the "Cleanroom" tab.

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
