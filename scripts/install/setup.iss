; ============================================
; Live Photo Box Inno Setup 安装包脚本
; 配合 build-release.ps1 使用
; 包含 GUI + CLI（livephotobox.exe 及别名）
; ============================================

#define AppName "Live Photo Box"
#define AppPublisher "LengxiQwQ"
#define AppURL "https://github.com/LengxiQwQ/live-photo-box"
#define AppExeName "Live Photo Box.exe"
#define SourceDir "..\..\publish\portable_x64"
#define IconFile "..\..\LivePhotoBox\Assets\Icons\AppIcon.ico"

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
LicenseFile=..\..\LICENSE
OutputDir=..\..\publish
OutputBaseFilename=Live-Photo-Box-v{#VERSION_SHORT}-x64-setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; ── 安装行为 ────────────────────────────────────────────────
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no
; 安装 / 卸载时刷新环境变量（PATH 变更生效）
ChangesEnvironment=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; CLI 添加到 PATH（默认勾选；不用 checkedonce → 升级时沿用上次勾选状态，而不是强制不勾）
Name: "addpath"; Description: "Add Live Photo Box to system PATH (allows livephotobox / lpb from any terminal)"; GroupDescription: "CLI tools:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

; ── PATH 环境变量（仅安装时，用户勾选 addpath Task） ─────────
[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
    Tasks: addpath; Check: NeedsAddPath('{app}')

; ── 卸载时清理运行时产生的数据 ──────────────────────────
; 注意：{app} 中的安装文件由 Inno Setup 自动根据安装日志删除；
; [UninstallDelete] 负责清理安装后运行时生成的文件。
[UninstallDelete]
; 整个 %LOCALAPPDATA%\LivePhotoBox（含 Logs、WebView2、Dumps 等）
Type: filesandordirs; Name: "{localappdata}\LivePhotoBox"
; 临时文件：首页示例复制到 %TEMP% 的样本数据
Type: filesandordirs; Name: "{%TEMP}\LivePhotoBox_Demo"
; 临时文件：更新下载残留在 %TEMP% 的文件（万一中断没清干净）
Type: filesandordirs; Name: "{%TEMP}\LivePhotoBox_Update"

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall runasoriginaluser

; ── PATH 添加 / 移除 ──────────────────────────────────────
[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + UpperCase(Param) + ';', ';' + UpperCase(OrigPath) + ';') = 0;
end;

// 卸载时从 PATH 移除安装目录，并清理残留的应用文件夹
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  OrigPath, NewPath: string;
  AppPath: string;
  PosIdx: Integer;
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    AppPath := ExpandConstant('{app}');

    // 1. 从系统 PATH 中移除安装目录
    if RegQueryStringValue(HKEY_LOCAL_MACHINE,
      'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
      'Path', OrigPath) then
    begin
      NewPath := OrigPath;
      PosIdx := Pos(';' + UpperCase(AppPath) + ';', ';' + UpperCase(NewPath) + ';');
      if PosIdx > 0 then
      begin
        PosIdx := PosIdx - 1;
        if PosIdx = 0 then
          NewPath := Copy(NewPath, Length(AppPath) + 2, MaxInt)
        else if PosIdx + Length(AppPath) >= Length(NewPath) then
          NewPath := Copy(NewPath, 1, PosIdx - 1)
        else
          NewPath := Copy(NewPath, 1, PosIdx - 1) + Copy(NewPath, PosIdx + Length(AppPath) + 1, MaxInt);

        if NewPath <> OrigPath then
          RegWriteStringValue(HKEY_LOCAL_MACHINE,
            'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
            'Path', NewPath);
      end;
    end;

    // 2. 删除应用安装目录（卸载器自身在此目录中，无法直接删除；
    //    用 cmd 延迟 3 秒后执行，等卸载器进程退出后清理）
    Exec('cmd.exe', '/C (ping -n 3 127.0.0.1 >nul) && rmdir /S /Q "' + AppPath + '"',
         '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
