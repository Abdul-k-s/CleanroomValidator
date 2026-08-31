@echo off
REM CleanroomValidator Quick Uninstall
REM Double-click this file to uninstall

echo.
echo ========================================
echo   CleanroomValidator Uninstaller
echo ========================================
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Uninstall

echo.
pause
