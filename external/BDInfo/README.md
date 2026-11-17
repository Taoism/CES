# BDInfo engine (third-party, LGPL 2.1)

The Blu-ray scan and report engine of **BDInfo** — https://github.com/UniqProject/BDInfo
(originally by Cinema Squid), version 0.7.6.2, upstream commit
`4e17c617278152983ce8e13201063f791c601f19` (2023-02-13).

Licensed under the **GNU Lesser General Public License 2.1 or later** (see `LICENSE`). It is *not* covered by
CES's MIT license. It is built as its own assembly, `BDInfo.dll`, which CES loads at run time; you may replace
it with your own build of this code (modified or not), as long as the public API used by CES stays the same.

## What is included

- `BDROM/*.cs` — the disc, playlist, stream and codec parsers, from `BDInfo/BDROM`.
- `ToolBox.cs` — unchanged.
- `BDInfoReport.cs` — the report generator, from `FormReport.cs`.
- `BDInfoScanner.cs` — the scan driver and playlist list, from `FormMain.cs`.
- `BDInfoSettings.cs` — replaces the `BDInfoSettings` class in `FormSettings.cs`.

The WinForms application (main window, settings, charts) is not included. It uses `DiscUtils.Udf` for disc images,
as upstream does.

## Changes made for CES (2026-11-17)

Every changed file says so in its header.

| File | Change |
| --- | --- |
| `BDROM/BDROM.cs` | New `BDROM(string path, Stream image)` constructor opens a disc image from an already open seekable stream (an ISO inside a RAR set); `IoStream` is a `Stream`; `GetVolumeInformation` (kernel32) is only called on Windows. |
| `BDROM/TSStreamFile.cs` | New `Scan(playlists, isFullScan, CancellationToken)` overload; the scan can be cancelled between 5 MB reads (upstream aborts the scan thread, which .NET no longer supports). |
| `BDInfoReport.cs` | `FormReport.Generate` as the static `BDInfoReport.Generate`, returning the report text; no autosave, combo boxes or chart. The report text is unchanged. |
| `BDInfoScanner.cs` | `ScanBDROMWork`/`ScanBDROMThread`, the playlist grouping and sort order from `FormMain.cs`, and `ScanBDROMResult`, without WinForms; progress by callback. |
| `BDInfoSettings.cs` | Plain static properties with the upstream defaults instead of WinForms application settings. |
| `BDInfo.csproj` | New SDK-style project for .NET 10; defines `BETA` so Debug builds don't write per-file `StreamLogs`. |

To update: copy the newer `BDInfo/BDROM/*.cs` and `ToolBox.cs` over these files and re-apply the changes above.
