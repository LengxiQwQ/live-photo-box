; ============================================
; LivePhotoBox Inno Setup 安装包脚本
; 配合 build-release.ps1 使用
; ============================================

#define AppName "Live Photo Box"
#define AppPublisher "LengxiQwQ"
#define AppURL "https://github.com/LengxiQwQ/live-photo-box"
#define AppExeName "Live Photo Box.exe"
#define SourceDir "..\publish\portable_x64"
#define IconFile "..\LivePhotoBox\Assets\Icons\AppIcon.ico"

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
AppVerName={#AppName} {#VERSION_SHORT}
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
UninstallDisplayName={#AppName} {#VERSION_SHORT}
Compression=lzma2
SolidCompression=no
LZMAUseSeparateProcess=yes
WizardStyle=modern
AppCopyright=Copyright (C) 2026 LengxiQwQ
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright (C) 2026 LengxiQwQ. Licensed under GPL v3.0
VersionInfoDescription=Display & process Apple Live Photos on Windows
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#VERSION}
VersionInfoProductTextVersion={#VERSION_SHORT}
VersionInfoVersion={#VERSION}
; ── 系统要求 ────────────────────────────────────────────────
; 只支持 64 位系统
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 要求 Windows 10 1809 或更高（匹配 .NET 9 + WinAppSDK 的最低要求）
MinVersion=10.0.17763
; ── 安装行为 ────────────────────────────────────────────────
; 装到 Program Files 必须管理员权限（Inno Setup 默认行为，显式声明更清晰）
PrivilegesRequired=admin
; 覆盖安装/卸载时自动关闭正在运行的应用，避免 .exe/.dll 被占用导致失败
CloseApplications=yes
RestartApplications=no
; 安装界面语言（跟随系统）
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; desktopicon 是 Inno Setup 内置 Task，Description 由 .isl 自动翻译
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; ── 卸载时清理运行时产生的用户数据 ──────────────────────────
; Inno Setup 默认只删除安装时复制的文件，运行期写入的数据不会自动清理。
; [UninstallDelete] 在卸载最后阶段执行（用户已确认卸载但文件尚未删除时）。
; Type: filesandordirs 会递归删除整个目录，files 只删除单个文件。
[UninstallDelete]
; 日志文件、崩溃转储
Type: filesandordirs; Name: "{localappdata}\LivePhotoBox\Logs"
; WebView2 用户数据（缓存、Cookie 等）
Type: filesandordirs; Name: "{localappdata}\LivePhotoBox\WebView2"
; 非打包模式的 JSON 设置文件（在安装目录下，卸载后无意义）
Type: files; Name: "{app}\appsettings.json"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall runasoriginaluser
