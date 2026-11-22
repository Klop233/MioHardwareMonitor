; SideScreen HUD (星澪硬件监控) — Inno Setup 6 Installer Script
; Supports both win-x64 and win-arm64 via preprocessor parameters:
;   ISCC.exe /DMyAppVersion=1.0.0 /DMyArch=x64 /DMyPublishDir=..\dist\publish\win-x64 /DMyOutputDir=..\dist\release /DMyOutputBaseFilename=SideScreenHUD-v1.0.0-win-x64-Setup installer\SideScreenHUD.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef MyPublishDir
  #define MyPublishDir "..\dist\publish\win-x64"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\dist\release"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "SideScreenHUD-v" + MyAppVersion + "-win-" + MyArch + "-Setup"
#endif

#define MyAppName "星澪硬件监控"
#define MyAppEnName "SideScreen HUD"
#define MyAppPublisher "星澪科技 (MioLab)"
#define MyAppExeName "SideScreenHUD.exe"

[Setup]
AppId={{E8B42D1C-6A73-4F29-9C41-7B5D8E12A40F}
AppName={#MyAppName} ({#MyAppEnName})
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion} ({#MyArch})
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\SideScreenHUD
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
AppMutex=SideScreenHUD_SingleInstance_Mutex_v1
CloseApplications=force
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} ({#MyAppEnName})

#if MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
#if VER >= 0x06030000
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif
#endif

[Languages]
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式 (Create a desktop shortcut)"; GroupDescription: "附加选项 (Additional options):"
Name: "autostart"; Description: "开机自动启动 (Launch automatically on Windows startup)"; GroupDescription: "附加选项 (Additional options):"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SideScreenHUD"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即启动 {#MyAppName} (Launch {#MyAppEnName})"; Flags: nowait postinstall skipifsilent
