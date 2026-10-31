@echo off
rem ==========================================================
rem  Hardware Monitor Overlay - full build
rem  main exe + setup.exe + portable zip + SHA256SUMS.txt
rem ==========================================================
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" ( echo [ERROR] csc.exe not found & exit /b 1 )

echo [1/5] Generate icon ...
if not exist "icon.ico" (
  "%CSC%" -nologo -target:exe -out:"tools\MkIcon.exe" "tools\MkIcon.cs" || exit /b 1
  "tools\MkIcon.exe" "icon.ico" || exit /b 1
)

echo [2/5] Compile HardwareMonitor.exe ...
"%CSC%" -nologo -target:winexe -optimize+ ^
  -out:"HardwareMonitor.exe" -win32manifest:"app.manifest" -win32icon:"icon.ico" ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "src\Native.cs" "src\Util.cs" "src\Settings.cs" "src\Sampler.cs" "src\Renderer.cs" "src\OverlayForm.cs" "src\Program.cs" || exit /b 1

echo [3/5] Compile setup.exe ...
"%CSC%" -nologo -target:winexe -optimize+ ^
  -out:"setup.exe" -win32manifest:"app.manifest" -win32icon:"icon.ico" ^
  -resource:HardwareMonitor.exe,HwMonPayload.exe ^
  -r:System.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll ^
  "installer\Setup.cs" || exit /b 1

echo [4/5] Generate docs (preview + selftest report) ...
if not exist "docs" mkdir "docs"
"HardwareMonitor.exe" --selftest "docs\verification-selftest.txt"
"HardwareMonitor.exe" --render "docs\preview.png"

echo [5/5] Package release ...
if not exist "release" mkdir "release"
del /q "release\*.zip" "release\SHA256SUMS.txt" 2>nul
powershell -NoProfile -Command "Compress-Archive -Path 'HardwareMonitor.exe','README.md','LICENSE','RELEASE_NOTES.md' -DestinationPath 'release\HardwareMonitor-1.0.0-portable.zip' -Force"
powershell -NoProfile -Command "$o=@(); foreach($x in @('HardwareMonitor.exe','setup.exe','release\HardwareMonitor-1.0.0-portable.zip')){ $o += ((Get-FileHash $x -Algorithm SHA256).Hash.ToLower() + '  ' + (Split-Path $x -Leaf)) }; $o | Set-Content -Encoding ascii 'release\SHA256SUMS.txt'"

echo.
echo Build complete:
dir /b "HardwareMonitor.exe" "setup.exe"
dir /b "release"
endlocal