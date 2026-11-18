@echo off
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process -Verb RunAs -FilePath \"%~f0\"" & exit /b)
echo Installing Gnosis VPN: every step is listed below with its time.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
if errorlevel 1 (echo. & echo Install FAILED - please take a photo of this window.) else (echo. & echo Gnosis VPN is installed and starting.)
pause
