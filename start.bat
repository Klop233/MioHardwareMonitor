@echo off
chcp 65001 >nul
cd /d "%~dp0"
set EXE=bin\Release\net10.0-windows\SideScreenHUD.exe
if not exist "%EXE%" (
  echo [SideScreen HUD] 首次运行正在编译 Release 版本...
  dotnet build -c Release
)
if exist "%EXE%" (
  start "" "%EXE%"
) else (
  echo [错误] 编译失败，请检查 .NET SDK 是否安装正确。
  pause
)
