# CleanroomValidator Installer

Creates a **single EXE installer** for CleanroomValidator.

## Prerequisites

1. **Inno Setup 6** - Download from https://jrsoftware.org/isdl.php
2. **.NET 8.0 SDK** - Download from https://dotnet.microsoft.com/download

## Build the Installer

Double-click `Build.bat` or run:

```batch
cd Installer
Build.bat
```

## Output

```
Installer\Output\CleanroomValidator_v1.0.0_Setup.exe
```

This single EXE:
- Contains all files compressed
- Shows a simple install wizard
- Installs to the correct Revit 2025 location
- Adds uninstaller to Control Panel

## What Users See

```
┌─────────────────────────────────────────────────────────┐
│  Welcome to CleanroomValidator Setup                    │
│                                                         │
│  This will install CleanroomValidator v1.0.0            │
│  for Revit 2025.                                        │
│                                                         │
│  Cleanroom compliance validation tools for GMP          │
│  and ISO standards.                                     │
│                                                         │
│  Click Next to continue.                                │
│                                                         │
│                     [Next >]  [Cancel]                  │
└─────────────────────────────────────────────────────────┘
```

## Install Location

```
%AppData%\Autodesk\Revit\Addins\2025\
├── CleanroomValidator.addin
└── CleanroomValidator\
    ├── CleanroomValidator.dll
    ├── CleanroomValidator.deps.json
    └── CleanroomValidator.runtimeconfig.json
```

## Uninstall

Control Panel → Programs → CleanroomValidator for Revit 2025 → Uninstall
