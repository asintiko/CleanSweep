# CleanSweep

Windows system optimizer and disk cleanup tool. Single .exe, no installation required.

Built with WPF (.NET 8).

## Features

- **Duplicate Photos** — perceptual hash (pHash) via DCT, auto-selects best quality as original
- **Duplicate Files** — MD5 hash matching with size pre-filter
- **Junk Cleanup** — temp files, browser cache (Chrome/Edge/Firefox/Brave), Discord/Slack/Teams cache, Windows logs, thumbnails, update cache, recent docs, Recycle Bin
- **Large Files** — finds files over 50 MB sorted by size
- **Disk Analyzer** — visual breakdown of folder sizes with proportional bar chart
- **Empty Folders** — finds and removes zero-content directories
- **Startup Manager** — view and remove HKCU/HKLM Run registry entries
- **Broken Shortcuts** — scans .lnk files pointing to missing targets

## Safety

System folders are automatically excluded from all scans:

- Windows, System32, SysWOW64, WinSxS, servicing
- Program Files, Program Files (x86), ProgramData, WindowsApps
- System Volume Information, $Recycle.Bin

Before scanning, you choose: specific folders or the entire computer.

## Build

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build -c Release
```

### Publish single-file .exe

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `publish/CleanSweep.exe`

### Cross-compile from macOS/Linux

The project uses `EnableWindowsTargeting` — builds from any OS:

```bash
dotnet build -c Release
```

## Auto-Update

On startup, CleanSweep checks GitHub Releases for new versions. Tag a release as `vX.Y.Z` and attach `CleanSweep.exe` — users get notified automatically.

## License

[MIT](LICENSE)
