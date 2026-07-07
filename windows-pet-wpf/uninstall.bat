@echo off
setlocal
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
if errorlevel 1 (
  echo.
  echo 卸载失败，请先关闭桌宠后重试。
  pause
  exit /b 1
)
echo.
echo 卸载完成，按任意键关闭。
pause >nul
