@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo [SideScreen HUD] 正在编译并打包（便携 ZIP + Windows 安装包 Setup.exe）...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\build-release.ps1" %*
if errorlevel 1 (
  echo [错误] 编译或打包失败，请查看上方日志。
  pause
  exit /b 1
)
echo.
echo [完成] 产物位于 dist\release 目录。
explorer "%~dp0dist\release"
