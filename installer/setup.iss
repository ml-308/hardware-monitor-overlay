; 硬件监控浮窗 · 安装程序（Inno Setup 6）
; 页面结构与选项由 common.iss 统一提供；这里只声明应用身份与要安装的文件。
; 构建：installer\build-setup.cmd   产物：dist\HardwareMonitor-Setup-1.1.0.exe

#define AppName "硬件监控浮窗"
#define AppSlug "HardwareMonitor"
#define DirName "HardwareMonitor"
#define AppVersion "1.1.0"
#define AppId "{{7A1C4E20-9B3D-4E58-8C71-2F6A9D5B4C13}"
#define AppExe "HardwareMonitor.exe"
#define AppRepo "hardware-monitor-overlay"
#define AppTagline "轻量硬件利用率浮窗：CPU / 内存 / GPU / 磁盘 / 网络，逐像素透明，可拖动缩放。"
#define IconPath "icon.ico"

; 常驻型工具：提供「登录时自动启动」选项
#define FeatureAutostart
#define RunValue "HwMonOverlay"

; 旧版自绘安装器（installer\Setup.cs）留下的痕迹，装完自动清理
#define LegacyUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\HwMonOverlay"
#define LegacyRunValue "HwMonOverlay"

#include "common.iss"

[Files]
Source: "{#AppRoot}\HardwareMonitor.exe"; DestDir: "{app}"; Flags: ignoreversion
; 用户调好的浮窗位置与显示项：已存在就不覆盖，卸载时也保留
Source: "{#AppRoot}\HwMon.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#AppRoot}\README.md"; DestDir: "{app}"
Source: "{#AppRoot}\LICENSE"; DestDir: "{app}"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon
