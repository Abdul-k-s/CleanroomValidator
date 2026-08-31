@echo off
REM CleanroomValidator Quick Install
REM Double-click this file to install

echo.
echo ========================================
echo   CleanroomValidator Installer
echo ========================================
echo.

powershell -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Install

echo.
pause
