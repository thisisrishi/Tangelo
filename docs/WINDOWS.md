# Tangelo for Windows — Usage Guide

Tangelo for Windows has the same features as the Mac app, adapted to Windows conventions.
This guide covers what's different; the [main usage guide](USAGE.md) describes every tool in detail.

- [Install](#install)
- [The wheels](#the-wheels)
- [Other ways to start](#other-ways-to-start)
- [Floats](#floats)
- [Differences from the Mac app](#differences-from-the-mac-app)
- [Settings and files](#settings-and-files)
- [Uninstall](#uninstall)
- [Troubleshooting](#troubleshooting)
- [Building from source](#building-from-source)

## Install

Download **TangeloSetup-x.y.z-x64.exe** from the [latest release](https://github.com/thisisrishi/Tangelo/releases/latest) and run it.
The installer is per-user (no administrator rights) and installs to `%LOCALAPPDATA%\Programs\Tangelo`.
During setup you can choose to:

- launch Tangelo when you sign in,
- add **Tangelo…** to the File Explorer right-click menu,
- create a desktop shortcut.

Prefer not to install? Use **Tangelo-windows-x64-portable.zip**: unzip anywhere and run `Tangelo.exe`.

> **SmartScreen:** until Tangelo's releases are code-signed, Windows may show *"Windows protected your PC"*.
> Click **More info → Run anyway**.

Requirements: Windows 10 (1809) or later, x64. Windows on ARM runs the x64 build under emulation.
Everything needed is bundled: the .NET runtime, ffmpeg and the background-removal model.

## The wheels

Start dragging files in File Explorer or on the desktop, then **tap** a modifier key (no need to hold it):

| Tap | Wheel |
|---|---|
| **Shift** | Conversions |
| **Ctrl** | Tools |
| **Alt** | Actions (Share, Float, Copy to…, Move to…, Copy Path, Terminal, Preview, Show in Explorer) |

The wheel appears under the cursor and fills in as soon as the drag moves over it. This is how Windows
works: it only tells an app what's being dragged once the drag reaches one of its windows. Drop on a petal
to run it; drop in the centre, or let go anywhere else, to cancel.

If you tap a modifier while dragging something that isn't files, such as selected text, the wheel closes on its own.

Change the shortcuts in **Settings → Shortcuts**. Choices: Shift, Ctrl, Alt, Ctrl+Shift, Ctrl+Alt, Alt+Shift, Win.

## Other ways to start

| Method | How |
|---|---|
| **Tray icon** | Click the Tangelo icon in the notification area, then drop files on the panel or click **Explorer Selection**. |
| **Explorer menu** | Right-click files → **Tangelo…** (on Windows 11: **Show more options → Tangelo…**). Several selected files open together. |
| **Floats** | Right-click files in a Float → Convert to / Tools / Actions. |
| **Tray menu** | Right-click the tray icon for New Float, Use Explorer Selection, Settings and Quit. |

## Floats

- Shake the cursor while dragging files to open a new Float under the cursor (closes again if you drop nothing).
- Drop files onto a Float to add them; drag the 📎 handle to take all files (or the selected ones) out.
- Click, Ctrl-click and Shift-click to select; **Ctrl+A** selects all; **Esc** clears.
- **Space** opens a preview; **Delete** removes files from the Float (files on disk are untouched).
- Double-click opens a file. Right-click shows every conversion, tool and action.
- The chevron collapses a Float to its title bar. Floats are restored when Tangelo restarts.

## Differences from the Mac app

| Mac | Windows |
|---|---|
| Menu-bar icon | Notification-area (tray) icon |
| AirDrop | **Share** (Windows share sheet: Nearby Share, Mail, apps) |
| Quick Look | **Preview** window (Explorer thumbnail, ← → to browse) |
| Terminal / iTerm / Ghostty / Warp | Windows Terminal / PowerShell / Command Prompt |
| Reveal in Finder | Show in Explorer |
| Pages / Numbers / Keynote conversions | DOC, XLS, PPT, PPTX and ODP via LibreOffice (if installed) |
| HEIC output | HEIC is input-only on Windows (the HEVC encoder is GPL-licensed); use AVIF, which is smaller |
| Vision OCR, face detection | Windows OCR (`Windows.Media.Ocr`) and `FaceDetector` — OCR uses your installed Windows languages |
| Vision subject lift | On-device U²-Netp model via ONNX Runtime |

## Settings and files

| What | Where |
|---|---|
| Settings, Float state | `%APPDATA%\Tangelo\settings.json` |
| History | `%APPDATA%\Tangelo\history.json` |
| Launch at login | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Tangelo` |
| Explorer menu | `HKCU\Software\Classes\*\shell\Tangelo` and `…\Directory\shell\Tangelo` |

**Undo** in History moves an operation's outputs to the Recycle Bin.

## Uninstall

**Settings → Apps → Installed apps → Tangelo → Uninstall.** This removes the app, its Explorer menu entry,
the launch-at-login entry and `%APPDATA%\Tangelo`. For the portable build, delete the folder, then turn off
**Launch at login** and **Explorer menu** in Settings first if you enabled them.

## Troubleshooting

**The wheel doesn't open.** Make sure you're dragging files and *tap* the modifier while the mouse button is
down. Some apps run as administrator; Windows doesn't let normal apps see input in those windows. You can
always use the tray panel or the Explorer menu instead.

**Copy Text says OCR isn't available.** Add a language that supports OCR in **Settings → Time & language →
Language & region** (most languages do).

**Presentation conversions are missing.** Install LibreOffice: `winget install TheDocumentFoundation.LibreOffice`.

**Check every engine on your PC:**

```powershell
& "$env:LOCALAPPDATA\Programs\Tangelo\Tangelo.exe" --selftest "$env:TEMP\tangelo-selftest" | Out-Host
```

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). The engines (`Tangelo.Core`) are
cross-platform and can be built and tested on macOS or Linux too.

```powershell
cd windows
./scripts/fetch-model.ps1                      # background-removal model (4.5 MB, checksum-verified)
dotnet build Tangelo.slnx -c Release
dotnet run --project src/Tangelo.SelfTest -c Release -f net10.0-windows10.0.19041.0   # 90+ engine checks
dotnet run --project src/Tangelo.App                                                   # run the app
./scripts/package.ps1 -Version 1.0.0           # self-contained app + portable zip + installer (needs Inno Setup)
```

| Project | Purpose |
|---|---|
| `src/Tangelo.Core` | Engines: formats, images (Magick.NET), audio/video (ffmpeg), documents, PDF, spreadsheets, archives, background removal, self-test |
| `src/Tangelo.Platform.Windows` | Windows OCR, text regions and face detection |
| `src/Tangelo.App` | WPF app: tray, drag monitor, wheels, Floats, editors, settings, Explorer integration |
| `src/Tangelo.SelfTest` | Console runner for the engine self-test |

`Tangelo.exe --demo <wheel-conversions|wheel-tools|wheel-actions|float|tray|settings|welcome|ToolName> files… [--snap out.png]`
opens a window without dragging, and can save it as an image (CI uses this for screenshots).
