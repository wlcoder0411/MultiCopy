; ============================================================================
; MultiCopy Inno Setup 安装包脚本
; 编译: ISCC.exe /DAppVersion="1.0.0" /DSourceExe="..\publish\win-x64\MultiCopy.exe" /DDistDir="..\dist" MultiCopy.iss
; 或通过 publish.ps1 自动调用
; ============================================================================

#ifndef AppVersion
  #define AppVersion "3.6"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\win-x64\MultiCopy.exe"
#endif
#ifndef DistDir
  #define DistDir "..\dist"
#endif

[Setup]
; 应用信息
AppName=MultiCopy
AppVersion={#AppVersion}
AppVerName=MultiCopy {#AppVersion}
AppPublisher=MultiCopy
AppPublisherURL=https://github.com/multicopy
AppSupportURL=https://github.com/multicopy
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}.0

; 安装目录（{autopf} 自动选 64 位 Program Files，非管理员安装则用本地 AppData）
DefaultDirName={autopf}\MultiCopy
DefaultGroupName=MultiCopy
DisableProgramGroupPage=yes

; 卸载
UninstallDisplayIcon={app}\MultiCopy.exe
UninstallDisplayName=MultiCopy

; 输出
OutputDir={#DistDir}
OutputBaseFilename=MultiCopySetup-{#AppVersion}

; 压缩
Compression=lzma2
SolidCompression=yes
LZMAUseSeparateProcess=yes

; 架构：仅 64 位
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 权限：普通用户即可安装（装到本地 AppData，无需管理员）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; 图标
SetupIconFile=..\src\MultiCopy\Assets\app.ico

; 界面
WizardStyle=modern
ShowLanguageDialog=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkedonce
Name: "startup"; Description: "开机自动启动"; GroupDescription: "其他选项:"; Flags: unchecked

[Files]
; 单文件自包含 exe，只需打包一个文件
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; 开始菜单
Name: "{group}\MultiCopy"; Filename: "{app}\MultiCopy.exe"
Name: "{group}\卸载 MultiCopy"; Filename: "{uninstallexe}"

; 桌面快捷方式（Task: desktopicon）
Name: "{autodesktop}\MultiCopy"; Filename: "{app}\MultiCopy.exe"; Tasks: desktopicon

; 开机自启（Task: startup，写入注册表 Run 项）
; 用 registry 段实现，见下方

[Registry]
; 开机自启（仅当选了 startup task 才写入）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MultiCopy"; ValueData: """{app}\MultiCopy.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; 安装后可选立即启动
Filename: "{app}\MultiCopy.exe"; Description: "{cm:LaunchProgram,MultiCopy}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; 卸载前关闭运行中的 MultiCopy（忽略错误，进程可能未运行）
Filename: "{cmd}"; Parameters: "/c taskkill /IM MultiCopy.exe /F"; Flags: runhidden; RunOnceId: "KillProcess"

[UninstallDelete]
; 卸载时清理应用数据目录（如有）
Type: dirifempty; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
