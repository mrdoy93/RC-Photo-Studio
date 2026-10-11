# RC Photo Studio

RC Photo Studio is a lightweight Windows desktop application for reusable Rush ID and regular photo print packages.

## Current prototype features

- Large thumbnail-button front menu
- Persistent custom package library
- Automatic Rush ID package builder for 2 × 2 inch, 1.5 × 1.5 inch, 1 × 1 inch, and 1.377 × 1.77 inch passport pictures
- Eight-inch, gap-free Rush ID printable width: mixed 1 × 1 pictures fill available space beside the 2 × 2 row first, then continue across full rows below; automatic page overflow
- Built-in factory catalog for regular photo packages
- Create, edit, duplicate, search, filter, and delete packages
- Mixed photo sizes and quantities in one package
- 4R, 5R, A4, Letter, and Legal paper presets
- Portrait, landscape, and automatic orientation
- Drag-and-drop JPG, PNG, BMP, GIF, and TIFF photos
- Repeat-one-photo, multiple-photo, and fill-page placement modes
- Live multi-page paper preview
- Optional non-printing grid and printable cutting guides
- Installed Windows printer selection and native printer settings
- Direct printing through the selected Windows printer driver
- Adaptive light and dark themes
- Local package and theme storage; no internet connection required

## Build and run

Open `RCPhotoStudio.sln` in Visual Studio with the **.NET desktop development** workload, then build and run the `RCPhotoStudio` project.

This workspace can also build with the installed Windows MSBuild:

```powershell
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe' .\RCPhotoStudio.sln /t:Rebuild /p:Configuration=Release
```

The executable is written to `RCPhotoStudio\bin\Release\RCPhotoStudio.exe`.

## Local data

Package definitions and theme preference are stored in `%LOCALAPPDATA%\RC Photo Studio`.
On a new installation, the app seeds that folder with its built-in package catalog.
Existing Rush ID templates are migrated to the automatic Rush ID package; other saved packages are preserved.

## Printing note

Always confirm the paper size and media type in the printer driver's settings. Drivers expose different capabilities, and physical printers may impose non-printable margins unless their borderless mode is enabled.
