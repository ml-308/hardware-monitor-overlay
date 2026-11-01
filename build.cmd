@echo off
rem ==========================================================
rem  Hardware Monitor Overlay - build main program
rem  Uses the built-in .NET Framework compiler (csc.exe).
rem  No Visual Studio / .NET SDK required.
rem ==========================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" ( echo [ERROR] csc.exe not found & exit /b 1 )

if not exist "icon.ico" (
  echo [1/2] Generate icon ...
  "%CSC%" -nologo -target:exe -out:"tools\MkIcon.exe" "tools\MkIcon.cs" || exit /b 1
  "tools\MkIcon.exe" "icon.ico" || exit /b 1
)

echo [2/2] Compile HardwareMonitor.exe ...
"%CSC%" -nologo -target:winexe -optimize+ ^
  -out:"HardwareMonitor.exe" -win32manifest:"app.manifest" -win32icon:"icon.ico" ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "src\Native.cs" "src\Util.cs" "src\Settings.cs" "src\Sampler.cs" "src\Renderer.cs" "src\OverlayForm.cs" "src\Program.cs"
if errorlevel 1 exit /b 1

rem === 代码签名：彻底消除 SmartScreen「无法识别的应用」（未配置证书时自动跳过） ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign.ps1" -Path "%~dp0HardwareMonitor.exe"

echo Done: %~dp0HardwareMonitor.exe
endlocal
