# Contributing to Tangelo

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Reporting bugs

Open an issue with your macOS version, Tangelo version (menu bar → Settings), the file type
involved, what you did and what happened. If you can, attach the output of:

```sh
/Applications/Tangelo.app/Contents/MacOS/Tangelo --selftest /tmp/tangelo-selftest
```

## Development

```sh
swift build && .build/debug/Tangelo          # run a debug build
.build/debug/Tangelo --selftest /tmp/t       # run the engine self-test
./build.sh                                   # universal release app, DMG and zip in build/
```

Useful for UI work: `.build/debug/Tangelo --demo <wheel-conversions|wheel-tools|wheel-actions|float|welcome|TOOL> file…`
opens a wheel, Float or tool editor (for example `--demo trimVideo clip.mov`) without dragging.

## Where things live

| Area | Path |
|---|---|
| Format detection & conversion targets | `Sources/Tangelo/Core/Formats.swift` |
| Conversion dispatch | `Sources/Tangelo/Core/Converter.swift` |
| Engines | `Sources/Tangelo/Core/*Kit.swift`, `AudioPCM.swift` |
| Tool catalog (titles, icons, which files a tool applies to) | `Sources/Tangelo/Tools/ToolCatalog.swift` |
| Tool operations | `Sources/Tangelo/Tools/ImageTools.swift`, `MediaTools.swift` |
| Editors | `Sources/Tangelo/UI/Editors/` |
| Wheel & drag detection | `Sources/Tangelo/UI/Wheel/` |

### Adding a tool

1. Add a case to `FileTool` with a title, symbol, subtitle and `applies(to:count:)` rule.
2. Implement the operation in `Tools/` (pure functions that return output URLs).
3. Either mark it `runsImmediately` and handle it in `Dispatcher.run`, or add an editor and route it in `EditorRoot`.
4. Add a check to `App/SelfTest.swift` and document it in `docs/USAGE.md`.

## Guidelines

- Never modify the user's original files; write new files with `Output.url(for:ext:suffix:)`.
- Prefer Apple frameworks; external tools (ffmpeg, cwebp, LibreOffice) must stay optional.
- No network access, analytics or telemetry.
- Keep `swift build` warning-free and the self-test passing.
