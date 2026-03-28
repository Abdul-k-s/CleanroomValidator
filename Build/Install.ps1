# CleanroomValidator Installer Script
# Creates a release package and installs to Revit 2025

param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$AppName = "CleanroomValidator"
$RevitVersion = "2025"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$BuildDir = "$ScriptDir\Output"
$RevitAddinsPath = "$env:APPDATA\Autodesk\Revit\Addins\$RevitVersion"
$InstallPath = "$RevitAddinsPath\$AppName"

function Write-Header($text) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
}

function Build-Project {
    Write-Header "Building $AppName"
    
    # Clean output
    if (Test-Path $BuildDir) { Remove-Item $BuildDir -Recurse -Force }
    New-Item -ItemType Directory -Path $BuildDir | Out-Null
    
    # Build
    $projectPath = "$RootDir\CleanroomValidator\CleanroomValidator.csproj"
    Write-Host "Building project..." -ForegroundColor Yellow
    dotnet build $projectPath -c Release -o "$BuildDir\$AppName" --nologo
    
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    
    # Copy .addin file
    Copy-Item "$RootDir\CleanroomValidator.addin" "$BuildDir\$AppName.addin"
    
    Write-Host "Build successful!" -ForegroundColor Green
}

function Install-Addin {
    Write-Header "Installing $AppName"
    
    # Check if build exists
    if (-not (Test-Path "$BuildDir\$AppName\CleanroomValidator.dll")) {
        Write-Host "Build not found. Building first..." -ForegroundColor Yellow
        Build-Project
    }
    
    # Create install directory
    if (-not (Test-Path $InstallPath)) {
        New-Item -ItemType Directory -Path $InstallPath | Out-Null
    }
    
    # Copy files
    Write-Host "Copying files to $InstallPath..." -ForegroundColor Yellow
    Copy-Item "$BuildDir\$AppName\*" $InstallPath -Recurse -Force
    
    # Copy .addin manifest
    $addinContent = Get-Content "$BuildDir\$AppName.addin" -Raw
    $addinContent = $addinContent -replace '<Assembly>.*</Assembly>', "<Assembly>$InstallPath\CleanroomValidator.dll</Assembly>"
    Set-Content "$RevitAddinsPath\$AppName.addin" $addinContent
    
    Write-Host ""
    Write-Host "Installation complete!" -ForegroundColor Green
    Write-Host "Location: $InstallPath" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Please restart Revit to load the add-in." -ForegroundColor Yellow
}

function Uninstall-Addin {
    Write-Header "Uninstalling $AppName"
    
    # Remove files
    if (Test-Path $InstallPath) {
        Remove-Item $InstallPath -Recurse -Force
        Write-Host "Removed: $InstallPath" -ForegroundColor Yellow
    }
    
    if (Test-Path "$RevitAddinsPath\$AppName.addin") {
        Remove-Item "$RevitAddinsPath\$AppName.addin" -Force
        Write-Host "Removed: $RevitAddinsPath\$AppName.addin" -ForegroundColor Yellow
    }
    
    Write-Host ""
    Write-Host "Uninstall complete!" -ForegroundColor Green
}

function Create-Package {
    Write-Header "Creating Release Package"
    
    # Build first
    Build-Project
    
    # Create ZIP
    $version = "1.0.0"
    $zipPath = "$BuildDir\$AppName-v$version-Revit$RevitVersion.zip"
    
    Write-Host "Creating ZIP package..." -ForegroundColor Yellow
    
    # Create temp folder structure
    $tempDir = "$BuildDir\temp\$AppName"
    if (Test-Path "$BuildDir\temp") { Remove-Item "$BuildDir\temp" -Recurse -Force }
    New-Item -ItemType Directory -Path $tempDir | Out-Null
    
    Copy-Item "$BuildDir\$AppName\*" $tempDir -Recurse
    Copy-Item "$BuildDir\$AppName.addin" "$BuildDir\temp\"
    Copy-Item "$RootDir\README.md" "$BuildDir\temp\" -ErrorAction SilentlyContinue
    
    # Create ZIP
    Compress-Archive -Path "$BuildDir\temp\*" -DestinationPath $zipPath -Force
    
    # Cleanup
    Remove-Item "$BuildDir\temp" -Recurse -Force
    
    Write-Host ""
    Write-Host "Package created!" -ForegroundColor Green
    Write-Host "Location: $zipPath" -ForegroundColor Gray
}

# Main
if ($Install) {
    Install-Addin
}
elseif ($Uninstall) {
    Uninstall-Addin
}
elseif ($Package) {
    Create-Package
}
else {
    Write-Host ""
    Write-Host "$AppName Installer" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Usage:" -ForegroundColor Yellow
    Write-Host "  .\Install.ps1 -Install     Install to Revit $RevitVersion"
    Write-Host "  .\Install.ps1 -Uninstall   Remove from Revit $RevitVersion"
    Write-Host "  .\Install.ps1 -Package     Create release ZIP"
    Write-Host ""
}
