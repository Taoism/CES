# EncoderSpot

A modern, cross-platform take on **EncSpot** (Jon Dee, Guerillasoft, 2001–2005): point it at a folder of
MP3 or FLAC files and it tells you which encoder made each one, with which settings, and whether the
stream is intact. It runs on Windows, Linux (Ubuntu, Debian) and macOS, and is written in .NET 10
with an Avalonia GUI and a command-line tool.

## What it reads

**MP3 / MP2 / MP1**: every frame header, the Layer III side info (block types, scalefac_scale, scfsi,
preflag, big_values), the bit reservoir (main_data_begin) and ancillary data, Xing/Info + LAME tag
(preset, VBR method, lowpass, delay/padding, ReplayGain, **tag CRC and music CRC check**), VBRI (FhG),
frame CRCs, ID3v1/ID3v2/APE/Lyrics3 tags. It reports sync errors, truncated last frames, junk, and a
Xing frame count that doesn't match the stream.

**FLAC**: all metadata blocks, the vendor string, and a full decode of every frame: CRC-8/CRC-16 checks,
**MD5 verification**, subframe types, LPC orders and precision, Rice partitions, and stereo decorrelation.
From these it estimates the libFLAC compression level. When a tagger has replaced the vendor string
(e.g. Mutagen), it fingerprints FFmpeg-encoded FLAC from the frame layout instead.

Encoder guesses come with a confidence level (High = the encoder signed the file; Low = heuristics).

## GUI

Folder tree, a colour-coded file list (quality column like EncSpot), filter, problems-only view,
CSV/text export, drag & drop. Double-clicking a file opens its details window with the bitrate
histogram and graph, reservoir use, block types, big values, stereo modes, and FLAC predictor and
LPC-order charts. Opening one file from the command line ("Open with") goes straight to its details.

## CLI

```
encspot [-r] [-d] [-j] [-q] [-p] [-t N] <file or folder>...
  -r recursive   -d details   -j JSON   -q FLAC metadata only   -p only problems
exit code 0 = ok, 1 = some files have problems
```

## Build

```
dotnet build EncoderSpot.sln
dotnet test
dotnet publish src/EncoderSpot -c Release -r linux-x64 --self-contained
```
