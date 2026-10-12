# RC Printer & Scanner

A compact Windows desktop application for Epson and Brother printers and scanners. It uses installed Windows drivers, so no vendor SDK is bundled.

## Features

- Detects installed Epson and Brother scanners through Windows Image Acquisition (WIA)
- Detects installed USB and network printers, with Epson and Brother devices shown first
- Flatbed scanning by default, with automatic document feeder available
- A3, A4, Legal, and Letter scan sizes, with unsupported A3 and Legal options automatically hidden for the selected scanner
- Detects every paper size reported by the selected printer, including driver-defined custom sizes and dimensions
- 150, 200, 300, and 600 DPI scanning, with 300 DPI selected by default
- Automatic document-background cleanup and local text contrast enhancement for True Gray, Gray Error Diffusion, and Black & White scans
- Color, True Gray, Gray (Error Diffusion), and Black & White modes, with Color selected by default
- Continuous multipage scanning with preview, zoom, scrolling, manual full-resolution crop, whole-page rotation, delete, and rescan controls
- Live document page count in the footer
- Centered scan progress popup with percentage, current-stage text, and a cancel button
- Smallest, Balanced, and High quality PDF compression
- Combined multipage PDF output
- ZIP output containing one compressed PDF for each scanned page
- Combined PDF and individual-PDF ZIP actions always ask for a destination and filename before saving
- Automatic portrait or landscape PDF pages so rotated scans fill the largest possible page area
- Remembered save location with an in-app **Browse** button
- Every completed scan automatically saves a JPEG copy to that folder, with Windows Pictures as the default
- Native Epson/Brother printer properties
- Scan and print settings close when the user clicks outside or activates another application
- Clear error when the printer driver rejects the selected paper size
- Visible print paper, color, quality, and **Fit to selected print size** controls plus a manual 1–999 copies box with decrease/increase buttons
- A larger centered **Direct Print or Copy** chooser titled **Select copy to Print**, with five persistent presets named **Copy 1** through **Copy 5**
- Left-clicking a Copy preset scans and prints immediately; right-clicking it opens that preset's paper, color, quality, copies, and fit settings
- The Copy chooser remains open after starting a direct-print job, with its buttons disabled until the job finishes
- Copy settings use explicit **OK** and **Cancel** actions; the Copy chooser stays open behind the editor, and confirmed settings persist after the app closes
- Optional automatic scaling that reduces scanned content to the selected printer paper's printable area
- Direct Copy printing scans one page and immediately sends it through the Windows XPS print queue, with no preview or print dialog
- High-contrast light and dark modes with light mode as the initial default; later user theme changes are remembered
- Fixed 760 × 576 window that opens in the center of the active screen while retaining full-size text and controls
- Custom printer/scanner logo in the title bar, taskbar, and packaged executable
- A larger primary **Scan** button, separate PDF actions, and five green direct-print presets

## Requirements

- Windows 10 version 2004 or later, or Windows 11
- .NET 8 Desktop Runtime
- The Epson or Brother printer/scanner driver installed in Windows
- Windows Image Acquisition enabled for scanning

## Build and run

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --configuration Release
```

Create the lightweight framework-dependent single-file release:

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained false --output dist/PrinterScanner-win-x64 -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

Run `dist\PrinterScanner-win-x64\PrinterScanner.exe` on a Windows x64 computer with the .NET 8 Desktop Runtime installed.

## Device notes

WIA and printer capabilities come from the installed device driver. If a scanner rejects an optional resolution, source, or color request, the app retains the closest driver-supported setting. Gray (Error Diffusion) is generated inside the app when the driver only supplies grayscale data.

Printer Properties applies the vendor driver's settings to direct print jobs for the current app session without changing the printer's Windows defaults.
