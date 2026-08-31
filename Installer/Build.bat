@echo off
REM ============================================
REM CleanroomValidator Installer Builder
REM Creates a single EXE installer
REM ============================================

setlocal enabledelayedexpansion

echo.
echo ============================================
echo   CleanroomValidator Installer Builder
echo ============================================
echo.

set SCRIPT_DIR=%~dp0
set ROOT_DIR=%SCRIPT_DIR%..
set FILES_DIR=%SCRIPT_DIR%Files
set OUTPUT_DIR=%SCRIPT_DIR%Output

REM Check for Inno Setup
set ISCC="C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist %ISCC% (
    set ISCC="C:\Program Files\Inno Setup 6\ISCC.exe"
)
if not exist %ISCC% (
    echo ERROR: Inno Setup 6 not found!
    echo.
    echo Please install from: https://jrsoftware.org/isdl.php
    echo.
    pause
    exit /b 1
)

REM Check for dotnet
where dotnet >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: .NET SDK not found!
    echo Please install .NET 8.0 SDK
    pause
    exit /b 1
)

REM Step 1: Clean
echo [1/4] Cleaning previous builds...
if exist "%FILES_DIR%" rmdir /s /q "%FILES_DIR%"
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
mkdir "%FILES_DIR%"

REM Step 2: Build DLL
echo [2/4] Building CleanroomValidator...
dotnet build "%ROOT_DIR%\CleanroomValidator\CleanroomValidator.csproj" -c Release -o "%FILES_DIR%" --nologo -v q
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Build failed!
    pause
    exit /b 1
)

REM Step 3: Copy addin manifest
echo [3/4] Preparing files...
copy "%ROOT_DIR%\CleanroomValidator.addin" "%FILES_DIR%\" >nul

REM Update assembly path in addin file
powershell -Command "(Get-Content '%FILES_DIR%\CleanroomValidator.addin') -replace '<Assembly>.*</Assembly>', '<Assembly>CleanroomValidator\CleanroomValidator.dll</Assembly>' | Set-Content '%FILES_DIR%\CleanroomValidator.addin'"

REM Step 4: Build installer
echo [4/4] Creating installer...
%ISCC% "%SCRIPT_DIR%Setup.iss" /Q
if %ERRORLEVEL% NEQ 0 (
    echo ERROR: Installer creation failed!
    pause
    exit /b 1
)

echo.
echo ============================================
echo   Build Complete!
echo ============================================
echo.
echo Output: %OUTPUT_DIR%\CleanroomValidator_v1.0.0_Setup.exe
echo.
echo This single EXE contains everything needed.
echo Just run it to install!
echo.

pause
exit /b 0
