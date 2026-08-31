@echo off
echo ============================================
echo   CleanroomValidator MSI Builder
echo ============================================
echo.

echo [1/2] Building CleanroomValidator...
cd /d "%~dp0..\..\CleanroomValidator"
dotnet build -c Release -o bin\Release\net8.0-windows

echo.
echo [2/2] Building MSI Installer...
cd /d "%~dp0"
dotnet build CleanroomValidator.Installer.wixproj -c Release

echo.
echo ============================================
echo Done! MSI is at:
echo Installer\MSI\bin\Release\CleanroomValidator_Setup_v1.0.0.msi
echo ============================================
pause
