# Tangelo Usage Guide

- [Ways to start an operation](#ways-to-start-an-operation)
- [The wheels](#the-wheels)
- [Conversions](#conversions)
- [Image tools](#image-tools)
- [Video tools](#video-tools)
- [Audio tools](#audio-tools)
- [PDF and file tools](#pdf-and-file-tools)
- [Actions](#actions)
- [Floats](#floats)
- [History and Undo](#history-and-undo)
- [Settings](#settings)
- [Troubleshooting](#troubleshooting)

---

## Ways to start an operation

| Method | How |
|---|---|
| **Wheel** | While dragging files, tap ⇧ (convert), ⌘ (tools) or ⌃ (actions). Drop on a petal. |
| **Menu-bar drop zone** | Click the menu-bar icon, drop files on the dashed area, choose Convert / Tools / Actions. |
| **Finder Selection** | Select files in Finder, click the menu-bar icon → **Finder Selection**. |
| **Float** | Right-click files in a Float → Convert to / Tools / Actions. |
| **Open With** | Finder → right-click → Open With → Tangelo puts files in a new Float. |

Results are saved **next to the original** by default with a descriptive suffix
(`photo compressed.jpg`, `clip trimmed.mp4`). Existing files are never overwritten;
Tangelo adds `2`, `3`… instead. Originals are never modified, except by **Batch Rename**,
which renames them on purpose.

A progress card appears in the top-right corner of the screen. Click a finished card to reveal its
output in Finder.

## The wheels

- The wheel opens centred on the cursor. The **center** shows what you're dragging; dropping there cancels.
- Up to 12 petals are shown. When more options exist, the last petal is **More…**, which opens a full chooser.
- Moving off the wheel or releasing the mouse elsewhere closes it.
- Change or disable each wheel's modifier in **Settings → Shortcuts**. ⌥ Option isn't used by
  default because Finder uses it to toggle copy/move while dragging.

Mixed selections (for example images and PDFs) only show conversions that every file supports.

## Conversions

### Images
JPG, PNG, WebP, HEIC, AVIF, TIFF, GIF, BMP, ICO and PDF. SVG and PDF are accepted as inputs.
- Lossy quality comes from **Settings → Conversions** (default 85%).
- EXIF, GPS, TIFF and IPTC metadata is preserved unless you turn that off.
- ICO output is letterboxed onto a transparent 256×256 canvas.
- Animated GIFs stay animated when converted to GIF.

### Video
- MP4/MOV/M4V use AVFoundation (H.264, or HEVC if **Prefer HEVC** is on).
- MKV, WebM, AVI and WMV use ffmpeg.
- **GIF** uses the GIF frame rate and width from Settings (defaults 12 fps, 640 px, first 30 s).
  Use the **Make GIF** tool to pick a range.
- MP3/M4A/WAV extract the audio track.

### Audio
M4A, WAV, AIFF and FLAC are encoded natively; MP3, OGG, Opus and WMA use ffmpeg.
Bitrate comes from **Settings → Conversions** (default 192 kbps).

### Documents
DOCX, DOC, ODT, RTF, RTFD, HTML, TXT, Markdown, EPUB, Pages and PDF, in any direction.
- **Markdown → anything** supports headings, bold/italic/strikethrough, links, images, lists,
  block quotes, code blocks and tables.
- **Anything → Markdown** infers headings from font size and keeps emphasis, links, lists and tables.
- **PDF → text formats** uses the PDF's text layer and falls back to **OCR** for scanned pages.
- **PDF → PNG/JPG** writes one image per page into a `<name> pages` folder.
- **Pages** in either direction uses the Pages app if installed. Without it, Tangelo reads the
  preview embedded in the `.pages` file.
- **EPUB** output is a valid EPUB 3 with a table of contents.

### Spreadsheets
CSV, TSV, XLSX, ODS, JSON, Numbers (needs Numbers) and XLS (needs LibreOffice) as inputs.
Outputs: XLSX, CSV, TSV, JSON (array of objects keyed by the header row), HTML table,
Markdown table and PDF (landscape). Only the first sheet of a workbook is converted.

### Presentations
PPTX, PPT and Keynote → PDF, PPTX or PNG (one image per slide). Uses Keynote if installed,
otherwise LibreOffice.

### Subtitles
SRT ↔ VTT, or plain TXT (cue text only).

### Archives
Dragging any files or folders onto ZIP, TAR or GZ creates a single archive (`Archive.zip` for
several items). Dragging an archive onto another archive format repacks it.

## Image tools

| Tool | Notes |
|---|---|
| **Compress** | Presets (High, Balanced, Small, Smallest), a **target size** (binary-searches quality, then downsizes if needed), or **reduce by %**. *Keep original format* keeps HEIC/WebP/AVIF/PNG. |
| **Adjust** | Exposure, contrast, saturation, warmth, tint, highlights, shadows, sharpen, noise reduction, vignette, grain, black & white. Press and hold the preview to compare with the original. Applies to every selected image. |
| **Annotate** | Arrow, rectangle, ellipse, line, pen, highlighter, text, magnifier bubble and numbered steps. Drag an annotation to move it. Delete removes the selected one; ↶ undoes the last. Annotations are placed proportionally, so one set can be applied to several images. |
| **Frame** | Puts the image on a gradient, solid colour, image or transparent background, with padding, rounded corners, shadow and a canvas ratio (1:1, 16:9, 4:3, 4:5, 9:16). Saves PNG. |
| **Crop** | Drag a rectangle, or pick a ratio. With several images, the same share of each image is cropped. |
| **Resize** | By percent, longest side, width, height or exact size; optionally change the format. |
| **Remove BG** | Lifts the main subject onto transparency with Vision (macOS 14+). Saves `… no background.png`. |
| **Copy Text** | OCR for images and PDFs. The text is copied to the clipboard and shown in an editable window, with **Save as Text File**. |
| **Redact** | Draw rectangles, or use **Detect Faces** / **Detect Text**. Styles: Blur, Pixelate, Solid. Use Solid for anything truly sensitive. |
| **Collage** | Grid, Row, Column or Featured layout; spacing, padding, corners, fill vs. fit, background colour; Shuffle and Reverse order. |
| **Read QR** | Finds QR codes and barcodes in images and PDF pages. A single result is copied automatically; links get an Open button. |
| **Metadata** | Searchable table of every field, with a warning when location data is present. Edit title, author, copyright and description losslessly, or **Remove All Metadata**. |

## Video tools

| Tool | Notes |
|---|---|
| **Trim** | Drag the handles or type start/end times. **Play Selected Range** previews. Uses passthrough (no re-encode) when possible. |
| **Crop** | Draw the crop over a frame; scrub to check other frames. |
| **Speed** | 0.25× to 4×; audio pitch is preserved. Works on several videos at once. |
| **Mute** | Removes audio without re-encoding. |
| **Join** | Combines the selected clips in name order. Clips of different sizes are letterboxed to the first clip's size. |
| **Split** | Add split points at the playhead, untick clips you don't want, then **Export Clips** (separate files) or **Export One Video** (join the kept clips). |
| **Snapshots** | Add frames at the playhead, or every N seconds. Saved as PNG (in a folder when there are several). |
| **Redact** | Draw rectangles, choose a style, and set each rectangle's time range. **Detect Faces in Frame** adds boxes for the current frame. |
| **Make GIF** | Range, frame rate (8–24 fps) and width (320–1080 px). |
| **Compress** | Presets (1080p HEVC, 1080p, 720p, 540p) or a **target size**: bitrate and resolution are chosen to fit. Never raises the bitrate above the source's. |
| **Normalize / Channels / Bleep** | Also work on video: the processed audio replaces the original track and the picture is copied untouched. |

## Audio tools

| Tool | Notes |
|---|---|
| **Trim** | Waveform with draggable range; optionally **trim silent ends** below a threshold (−70 to −20 dB). |
| **Normalize** | Targets of −16 dB (podcast/streaming), −14 dB (music), −23 dB (broadcast) or −11 dB, with a peak ceiling. |
| **Channels** | Mono or stereo; left/right volume (0–200%); Left only, Right only, Swap. |
| **Bleep** | Mark ranges on the waveform; replace with a 1 kHz tone or silence. |
| **Visualize** | Renders an MP4 of bars, wave, circle or mirror visuals with your audio. Choose accent and background colours, artwork, title and size (720p, 1080p, square, story). |
| **Compress** | 192 / 128 / 96 kbps or voice (64 kbps mono), or a target size. |

Audio results keep the source format when Tangelo can write it; otherwise they're saved as M4A.

## PDF and file tools

| Tool | Notes |
|---|---|
| **Make PDF** | One page per image, in name order. |
| **Merge PDF** | Merges PDFs (and images) in name order. |
| **Split PDF** | One file per page, or custom ranges like `1-3, 4, 7-9`. |
| **Organize** | Drag thumbnails to reorder; rotate, delete or reverse; optionally make all pages the same width. |
| **Compress** | Quality and DPI sliders, or a target size. Refuses to produce a file larger than the original. |
| **Extract** | ZIP, RAR, 7Z, TAR, GZ, TGZ, XZ, BZ2, ISO, CPIO. A single top-level folder is unwrapped and `__MACOSX` is removed. |
| **Rename** | Pattern with `{name}` `{n}` `{date}` `{folder}`, find & replace, case and start number, with a live preview. Refuses duplicate names. Safe for swaps (a↔b). |

## Actions

| Action | Notes |
|---|---|
| **AirDrop** | Opens the AirDrop sheet. |
| **Float** | Puts the files in a new Float. |
| **Copy to… / Move to…** | Pick a folder; name clashes get a number. |
| **Copy Path** | Copies POSIX paths, one per line. |
| **Terminal** | Opens the folder (or the file's folder) in your chosen terminal. |
| **Quick Look** | Previews the files. |
| **Reveal** | Selects the files in Finder. |

## Floats

- **Create** one by shaking while dragging, from the menu bar (**New Float**), with the Float action, or with Open With → Tangelo.
- **Add** files by dropping them on a Float.
- **Select** with click; ⌘/⇧-click for several; ⌘A selects all; Esc clears the selection.
- **Drag out**: drag a single tile, or drag the 📎 handle to take every selected file (or all files).
- <kbd>Space</kbd> opens Quick Look; <kbd>Delete</kbd> removes selected files from the Float (files on disk are untouched).
- **Right-click** for Open, Quick Look, Reveal, Convert to, Tools, Actions, Remove from Float.
- **Collapse** with the chevron. Closing a Float (×) never deletes files.
- Floats and their positions are restored after relaunch (**Settings → General**).

## History and Undo

The menu-bar popover lists recent operations; **Settings → History** shows the last 200.
**Undo** moves an operation's outputs to the Trash. **Reveal** shows them in Finder.

## Settings

| Tab | Options |
|---|---|
| **General** | Launch at login · where to save results · reveal results · sound & haptics · shake to Float · restore Floats · remove files from a Float after dragging out · terminal app |
| **Shortcuts** | Enable each wheel and choose its modifier (⇧, ⌘, ⌃, ⌥ or combinations) |
| **Conversions** | Image quality · preserve metadata · audio bitrate · prefer HEVC · GIF frame rate and width |
| **Helpers** | What's available (ffmpeg, cwebp, Pages, Numbers, Keynote, LibreOffice), install hints, custom ffmpeg path |
| **Formats** | Full compatibility list and tool reference |
| **History** | Past operations with Reveal and Undo |

## Troubleshooting

**The wheel doesn't open.** You need to be dragging *files* (not text) and **tap** the modifier
while the mouse button is held. Check **Settings → Shortcuts** to make sure the wheel is enabled
and no two wheels share a modifier. You can always drop files on the menu-bar popover instead.

**"ffmpeg is required…"** Run `brew install ffmpeg`, or set a custom path in **Settings → Helpers**.

**"Apple could not verify Tangelo"** See the first-launch note in the [README](../README.md#install).

**iWork conversion fails.** Allow Tangelo in **System Settings → Privacy & Security → Automation**.

**Run the self-test** to check every engine on your Mac:

```sh
/Applications/Tangelo.app/Contents/MacOS/Tangelo --selftest /tmp/tangelo-selftest
```
