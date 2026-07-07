@echo off
setlocal
cd /d "%~dp0"
powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (
  echo.
  echo Install failed. Keep the full publish folder together and try again.
  pause
  exit /b 1
)
echo.
echo Install complete. Press any key to close.
pause >nul
