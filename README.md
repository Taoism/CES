# NothingElseCompares2u

*(…but a frame-exact screenshot does.)*

A dark, cross-platform (Avalonia / .NET 10) comparison tool for Blu-ray, DVD and video files, with a matching
command line, `nec2u`.

## What it does

- **Sources**: MKV, MP4, M2TS/TS, VOB and other ffmpeg-readable files; Blu-ray folders (BDMV) and DVD folders
  (VIDEO_TS), read through their own playlists/IFOs (titles, segments, chapters, stream languages, angles); ISO images,
  mounted read-only with the OS (Windows `Mount-DiskImage`, Linux `udisksctl`, macOS `hdiutil`). Unencrypted discs only.
- **Frame-exact screenshots** of the same moment in every source: evenly spaced, random (seeded), per chapter or at given
  times; aspect correction for anamorphic video, HDR→SDR tone-mapping, optional deinterlace. The reference frame decides
  the time and every other source is aimed at its nearest matching frame, so MPEG-TS seek overshoot and frame-grid
  phase cannot shift a source by one frame. Viewer: single (1–9 to switch), wipe, side by side; synced zoom/pan with
  nearest-neighbour pixels; frame stepping; frame type and signal stats per shot.
- **Sync**: audio cross-correlation at several points, then refined on the picture to the exact frame. Reports drift
  (different speed/edit) and refuses unreliable matches.
- **Specs**: side-by-side table (container, bitrates, codec/profile, resolution, DAR/SAR, bit depth, chroma, colour,
  HDR10/HDR10+/Dolby Vision, mastering display, MaxCLL/FALL, audio tracks, subtitles…), differences highlighted.
- **Video analysis**: effective bit depth (8-bit padded into 10-bit), native resolution / upscale detection (least-squares
  descale at many heights and six kernels, harmonic-aware), detail spectrum and sharpness, black bars, limited-range
  violations, measured HDR brightness (fake/flat HDR), interlacing.
- **Audio analysis**: EBU R128 loudness, LRA, true peak, per-channel peak/RMS/noise floor/crest, bits really used
  (padded 16-bit in 24-bit), spectrum cutoff (lossy source in a lossless track, upsampled 44.1/48 kHz), silent channels,
  spectrogram.
- **Quality metrics**: PSNR, SSIM, VMAF, XPSNR against a reference, with automatic scaling and crop of bars, per-frame
  chart, worst frames → screenshots.
- **HTML report**: specs, findings, metrics charts and click/1–9 switchable screenshots.

## Requirements

ffmpeg + ffprobe (with libvmaf for VMAF; zscale for HDR tone-mapping), optionally MediaInfo CLI. Found next to the program
(`ffmpeg\`, `mediainfo\`), via Settings, or on PATH.

## Command line

```
nec2u info   <source…> [--titles]
nec2u sync   <reference> <other…>
nec2u shots  <source…> [-n 6 | --at 1:02:03,… | --random | --chapters] [--auto-sync] [--out DIR]
nec2u video  <source…> [--points 8] [--curve]
nec2u audio  <source…> [--track 1|all] [--full] [--out DIR]
nec2u metrics <reference> <other…> [--metrics psnr,ssim,vmaf,xpsnr] [--start T] [--length 60]
nec2u report <source…> [--metrics] [--out DIR]
```

Pick a disc title with `path@00800.mpls` or `path@3`; offsets with `--offset 2=-5.005`. See `nec2u --help`.

## Build

```
dotnet build NothingElseCompares2u.sln
dotnet test
```
