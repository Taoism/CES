# CES — Compare Enc Spot

A dark, cross-platform (Avalonia / .NET 10) tool with two modes and one command line, `ces`:

- **Spot** — point it at a folder of MP3 or FLAC files and it tells you which encoder made each one, with which
  settings, and whether the stream is intact (a modern take on EncSpot).
- **Compare** — Blu-ray, DVD and video releases side by side: frame-exact screenshots, sync, audio, video, quality
  metrics, remasters and subtitles.

CES joins two earlier tools: NothingElseCompares2u (Compare) and EncoderSpot (Spot). Both modes share one window,
menu and status bar; switch with the Spot / Compare buttons or Ctrl+1 / Ctrl+2. In Spot, select two or more files
and choose **Compare selected** to open them in Compare.

Both modes navigate the same way: a directory tree on the left (opening where you last were), buttons above it
(Spot: *Open folder*, *Add files*; Compare: *Add files*, *Add disc folder*, **Compare**), and the results on the right.
In Compare the tree also lists media files and tags Blu-ray (BD) and DVD folders; double-click one to add it as a
source. **Compare** lines the sources up with source 1 (audio match, refined to the exact frame).

## Spot mode

**MP3 / MP2 / MP1**: every frame header, Layer III side info (block types, scalefac_scale, scfsi, preflag, big_values),
the bit reservoir and ancillary data, Xing/Info + LAME tag (preset, VBR method, lowpass, delay/padding, ReplayGain,
**tag CRC and music CRC check**), VBRI (FhG), frame CRCs, ID3v1/ID3v2/APE/Lyrics3 tags. It reports sync errors,
truncated last frames, junk after the audio, and files whose content does not match their extension.

**FLAC**: all metadata blocks and the vendor string, plus a full decode of every frame: CRC-8/CRC-16 checks,
**MD5 verification**, subframe types, LPC orders and precision, Rice partitions and stereo decorrelation. From these it
estimates the libFLAC compression level, and it recognises FFmpeg-encoded FLAC from the frame layout when a tagger
(e.g. Mutagen) has replaced the vendor string. Spot mode needs no ffmpeg.

The file list has an EncSpot-style quality column; double-click a file for its details: bitrate histogram and graph,
reservoir use, block types, big values, stereo modes, FLAC predictor and LPC-order charts.

```
ces spot [-r] [-d] [-j] [-q] [-p] [-t N] <file or folder>…
  -r recursive   -d details   -j JSON   -q FLAC metadata only   -p only problems
exit code 0 = ok, 1 = some files have problems
```

## Compare mode

![Wipe between a master and a remaster](docs/screenshots/screenshots-wipe.png)

<sub>All screenshots are made from synthetic test sources (an ffmpeg mandelbrot zoom with generated audio): a master, a
re-graded and reframed "remaster", and a 720p WEB encode.</sub>

- **Sources**: MKV, MP4, M2TS/TS, VOB and other ffmpeg-readable files; Blu-ray folders (BDMV) and DVD folders
  (VIDEO_TS), read through their own playlists/IFOs (titles, segments, chapters, stream languages, angles); ISO images,
  mounted read-only with the OS (Windows `Mount-DiskImage`, Linux `udisksctl`, macOS `hdiutil`). Unencrypted discs only.
- **Frame-exact screenshots** of the same moment in every source: evenly spaced, random (seeded), per chapter or at given
  times; aspect correction for anamorphic video, HDR→SDR tone-mapping, optional deinterlace. The reference frame decides
  the time and every other source is aimed at its nearest matching frame, so MPEG-TS seek overshoot and frame-grid
  phase cannot shift a source by one frame. Viewer: single (1–9 to switch), wipe, side by side, **difference**
  (|A − B| × gain, with mean/changed share) and **flicker** (A ↔ B automatically); synced zoom/pan with
  nearest-neighbour pixels; frame stepping; frame type and signal stats per shot. **Detailed shots** mode samples
  keyframes across source 1 and picks well-lit, detailed moments instead of evenly spaced times.
- **Sync**: audio cross-correlation at several points, then refined on the picture to the exact frame. Search near the
  same time (±90 s) or the **whole title** (any offset). **Speed**: PAL speed-ups (23.976/24 → 25) are detected from the
  frame rates and handled everywhere (frames paired one to one). **Different edits**: probes along the whole title build
  sections with their own offset and mark parts that are missing from a source; each cut is then placed on the exact
frame by matching the pictures around it. **Telecine**: a 29.97 fps 3:2 pulldown source paired with 23.976 fps film
is detected and inverse-telecined (field matching + decimation) so frames pair one to one.
- **Comparisons are saved** as `.ces` files (older `.nec2u` files still open) (sources, titles, time maps, screenshot times, analysis, metrics, ranking,
  grade and frame checks);
  screenshots are re-grabbed on open. Recent comparisons in the File menu.
- **Specs**: side-by-side table (container, bitrates, codec/profile, resolution, DAR/SAR, bit depth, chroma, colour,
  HDR10/HDR10+/Dolby Vision, mastering display, MaxCLL/FALL, audio tracks, subtitles…), differences highlighted.
- **Video analysis**: effective bit depth (8-bit padded into 10-bit), native resolution / upscale detection (least-squares
  descale at many heights and six kernels, harmonic-aware), detail spectrum and sharpness, black bars, limited-range
  violations, measured HDR brightness (fake/flat HDR), interlacing.
- **Audio analysis**: EBU R128 loudness, LRA, true peak, per-channel peak/RMS/noise floor/crest, bits really used
  (padded 16-bit in 24-bit), spectrum cutoff (lossy source in a lossless track, upsampled 44.1/48 kHz), silent channels,
  spectrogram.
- **Quality metrics**: PSNR, SSIM, VMAF, XPSNR against a reference, with automatic scaling and crop of bars, per-frame
  chart, worst frames → screenshots. The first frames of both sources are checked against each other so a one-frame
  slip cannot spoil the scores.

  ![Quality metrics](docs/screenshots/quality-metrics.png)
- **Remasters**: on matched frames, the zoom/crop and shift between releases (open matte vs cropped, reframing,
  pan & scan) and the grade in CIELAB: tone curve (shadows/mids/highlights), contrast, saturation, colour cast — with a
  "re-graded" verdict. **Grade over time** charts the difference per point and tells one global grade from a
  shot-by-shot regrade. **Frame & restoration checks**: dropped or duplicated frames against the reference, dirt and
  specks removed (or added) by a restoration, and stabilisation (gate weave / shake removed). Works on still images too.

  ![Remaster tab](docs/screenshots/remaster.png)
- **Artefacts vs the reference**: grain removed (noise reduction), sharpening halos, more banding, softer picture; chroma upsampled
  from 4:2:0 (clean masters). **Audio vs the reference**: louder, brick-walled (crest/LRA), different EQ/master, and
  upmixed "fake" 5.1 (surrounds = copies or L−R, centre = L+R).
- **HDR**: HDR10+ / Dolby Vision per-shot metadata checked against the measured picture; wrong MaxCLL; DV profile 5 note.
- **Subtitles**: burn PGS / VobSub / text subtitles into screenshots (same language in every source); compare subtitle
  timing between releases (shift, spread, drift per hour, missing lines) — Blu-ray, DVD and file tracks.
- **Still images** (PNG/JPEG/TIFF/BMP/WebP) as sources: viewer, analysis and grade comparison work on pictures.
- **Share**: upload the screenshots to slow.pics (asks first; unlisted by default) and copy a BBCode/Markdown summary.
- **Fast**: screenshot cache (reopened comparisons and retakes are instant), parallel grabs; optional hardware decoding.
- **Rank releases**: point it at a folder of releases (File › Add folder of releases); every release is synced and
  measured against the reference in several windows across the title, then ranked (VMAF, 1 % low, PSNR, SSIM, bitrate).
- **HTML report**: specs, findings, metrics charts and click/1–9 switchable screenshots.

## More screenshots

| | |
|---|---|
| ![Side by side](docs/screenshots/screenshots-side-by-side.png) | ![Difference](docs/screenshots/screenshots-difference.png) |
| ![Specs](docs/screenshots/specs.png) | ![Video analysis](docs/screenshots/video-analysis.png) |
| ![Audio analysis](docs/screenshots/audio-analysis.png) | |

## Requirements

ffmpeg + ffprobe (with libvmaf for VMAF; zscale for HDR tone-mapping), optionally MediaInfo CLI. Found next to the program
(`ffmpeg\`, `mediainfo\`), via Settings, or on PATH.

## Command line

```
ces info   <source…> [--titles]
ces sync   <reference> <other…> [--whole] [--edits]
ces shots  <source…> [-n 6 | --at 1:02:03,… | --random | --chapters | --smart] [--subs eng] [--auto-sync] [--out DIR] [--slowpics] [--summary bbcode]
ces video  <source…> [--points 8] [--curve]
ces audio  <source…> [--track 1|all] [--full] [--out DIR]
ces metrics <reference> <other…> [--metrics psnr,ssim,vmaf,xpsnr] [--start T] [--length 60]
ces grade  <reference> <other…> [-n 8 | --at T,…] [--windows 5] [--no-temporal] [--curve]
                                                          (remaster: framing, grade, grade over time, frame checks)
ces subs   <reference> <other…> [--subs eng]           (subtitle timing)
ces rank   <reference> <release… | folder> [--windows 5] [--length 20] [--edits]
ces report <source…> [--metrics] [--out DIR]
```

Pick a disc title with `path@00800.mpls` or `path@3`; offsets with `--offset 2=-5.005`, speed with `--speed 2=pal`. See `ces --help`.

## Build

```
dotnet build CES.sln
dotnet test
```
