# CleanroomValidator — Revit 2025 Add-in

Verifies cleanroom airflow compliance with GMP (EU Annex 1) and ISO 14644 standards.

## Features

- **Compliance Check**: Verifies rooms against GMP-B/C/D or ISO-6/7/8 requirements
- **Auto-detection**: Reads room volume, supply CFM from MEP spaces, and pressure
- **Adjacency Analysis**: Detects adjacent rooms via doors for pressure cascade validation
- **Linked Model Support**: Optional use of a linked mechanical model for MEP data
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
<img width="1918" height="1015" alt="3" src="https://github.com/user-attachments/assets/5eb8621b-02ac-45bd-a61d-34cdc916a1e2" />
<img width="1915" height="1016" alt="2" src="https://github.com/user-attachments/assets/c77db85e-2b47-4467-8afe-5c6e36b76e0d" />


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

<img width="1920" height="1017" alt="Screenshot 2026-04-14 235132" src="https://github.com/user-attachments/assets/184a4f6a-db45-4e77-9c6a-67094e889e11" />


1. Click **Check Compliance**
2. Review results (ACH, pressure, recovery time)
3. Export to CSV if needed

### Set Space Type

<img width="1920" height="1013" alt="Screenshot 2026-04-14 234751" src="https://github.com/user-attachments/assets/09298ba0-0e87-4726-b4ee-2de5bbe1293e" />


1. Check all spaces that need to apply cleanroom parameters 
2. **Auto-Match All** set each space type by its name (uses 70% matches)
3. There is a colored legend that shows the percentage of matching names to make it easier for the user to check the space type
   
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
