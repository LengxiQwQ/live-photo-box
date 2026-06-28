; ============================================
; LivePhotoBox Inno Setup 安装包脚本
; 配合 build-release.ps1 使用
; ============================================

#define AppName "Live Photo Box"
#define AppPublisher "LengxiQwQ"
#define AppURL "https://github.com/LengxiQwQ/live-photo-box"
#define AppExeName "Live Photo Box.exe"
#define SourceDir "..\publish\portable_x64"
#define IconFile "..\Live Photo Box\Assets\Icons\AppIcon.ico"

; 版本号从 Package.appxmanifest 读取（命令行 /dVERSION=x.x.x.x 传入）
#ifndef VERSION
  #define VERSION "1.0.0.0"
#endif
#ifndef VERSION_SHORT
  #define VERSION_SHORT "1.0.0"
#endif

[Setup]
AppId={{B3E8F5A2-9D4C-4F1A-A6E7-8B2C0D5F3A9E}}
AppName={#AppName}
AppVerName={#AppName}
AppVersion={#VERSION}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=Live-Photo-Box-v{#VERSION_SHORT}-x64-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2
SolidCompression=no
WizardStyle=modern
AppCopyright=Copyright (C) 2026 LengxiQwQ
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 LengxiQwQ. Licensed under GPL v3.0
VersionInfoDescription=Display & process Apple Live Photos on Windows
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#VERSION}
VersionInfoProductTextVersion={#VERSION_SHORT}
VersionInfoVersion={#VERSION}
; 只支持 64 位系统
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 安装界面语言（跟随系统）
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
