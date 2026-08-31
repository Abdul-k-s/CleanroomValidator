# CleanroomValidator — Revit 2025 Add-in

Verifies cleanroom airflow compliance against GMP (EU Annex 1) and ISO 14644 standards.

## Features

- **Compliance Check**: Verifies rooms against GMP-B/C/D or ISO-6/7/8 requirements
- **Auto-detection**: Reads room volume, supply CFM from MEP spaces, pressure
- **Adjacency Analysis**: Detects adjacent rooms via doors for pressure cascade validation
- **Linked Model Support**: Optional use of linked mechanical model for MEP data
- **Summary Report**: Single-table view with export to CSV

## Installation

### Option 1: Quick Install (Recommended)

1. Download the latest release ZIP
2. Extract and double-click `Install.bat`
3. Restart Revit

### Option 2: PowerShell

```powershell
cd Build
.\Install.ps1 -Install
```

### Option 3: Manual

Copy files to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\CleanroomValidator\
```

And copy `CleanroomValidator.addin` to:
```
%APPDATA%\Autodesk\Revit\Addins\2025\
```

## Uninstall

Double-click `Uninstall.bat` or run:
```powershell
.\Install.ps1 -Uninstall
```

## Building from Source

### Requirements

- Visual Studio 2022
- .NET 8.0 SDK
- Revit 2025

### Build

```powershell
# Build and create release package
cd Build
.\Install.ps1 -Package
```

Output: `Build\Output\CleanroomValidator-v1.0.0-Revit2025.zip`

## Usage

### Set Cleanliness Class
1. Click **Set Class** button
2. Select GMP or ISO standard
3. Assign classes to rooms
4. Click **Apply Changes**

### Create Spaces
1. Click **Create Spaces**
2. Select room source (local or linked)
3. Map rooms to Space Types
4. Spaces are created with classifications

### Check Compliance
1. Click **Check Compliance**
2. Review results (ACH, pressure, recovery time)
3. Export to CSV if needed

## Standards Reference

### GMP (EU Annex 1)

| Grade | ACH | Pressure | Recovery |
|-------|-----|----------|----------|
| B | 40-60 | +15 Pa | 15 min |
| C | 20-40 | +10 Pa | 20 min |
| D | 10-20 | +10 Pa | — |

### ISO 14644

| Class | ACH | Pressure |
|-------|-----|----------|
| ISO-6 | 90-180 | +12.5 Pa |
| ISO-7 | 30-60 | +12.5 Pa |
| ISO-8 | 10-25 | +10 Pa |

## Project Structure

```
CleanroomValidator/
├── CleanroomValidator.sln
├── CleanroomValidator.addin
├── README.md
├── Build/
│   ├── Install.ps1          # PowerShell installer
│   ├── Install.bat          # Quick install
│   └── Uninstall.bat        # Quick uninstall
└── CleanroomValidator/
    ├── App.cs               # Ribbon UI setup
    ├── Commands/            # Revit commands
    ├── Data/                # Standards database
    ├── Models/              # Data models
    ├── Services/            # Business logic
    └── UI/                  # WPF windows
```

## License

MIT License - See LICENSE file
