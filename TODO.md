# CES TODO

CES = Compare + Spot modes, v0.3.1 (2026-11-17).

Done: whole-title sync, PAL speed, edits, sessions, ranking, difference/flicker, detailed shots (2026-11-15);
artefacts (grain/banding/halos/chroma), audio remaster/upmix checks, HDR10+/DV metadata, subtitles (burn-in, timing),
slow.pics + BBCode/Markdown, screenshot cache / hardware decoding, grade & framing (remasters), still images (2026-11-16);
grade + frame checks saved in sessions, DVD subtitle timing, frame-exact cuts, telecine (IVTC) pairing, metrics
first-frame alignment, dropped/duplicated frames, grade over time, dirt/speck and stabilisation checks, README
screenshots from synthetic sources (2026-11-16).

Open:
- [ ] slow.pics upload not tested live (publishes images) — try once with the user's OK
- [ ] Dolby Vision metadata parsing untested on a real DV file (none available)
- [ ] Chroma-origin check is hidden by lossy re-encodes (works on clean masters only)
- [ ] Sync: a remux whose video starts later than its audio (e.g. 0.25 s) can come out "not reliable" — the picture
      refine should rescue it
- [ ] Inverse telecine on real (not synthetic) pulldown material, incl. mixed film/video
- [ ] Release cards / per-GB value view, timeline strip, region zoom across sources
- [ ] Calibrate the untagged-MP3 heuristics (Xing old/new, Blade, FhG, GOGO...) against the original
      EncSpot 2.2 on the same files (reference/orig has the old installers) and against mp3guessenc output.
- [ ] More real-world encoder samples: FhG (ACM, fastenc, mp3enc), Xing, Blade, Helix, iTunes, GOGO, old LAME.
- [ ] Spot: lossy-source / upsampling check for FLAC: reuse Compare's spectrum cutoff + spectrogram
      (Ces.Core AudioAnalyzer); fake 24-bit via wasted bits; as a column + details tab.
- [ ] Packaging like TaoExif: Inno installer + win zip (bundled ffmpeg full + MediaInfo, updater), .deb, linux
      tarball (done: Debian 13 + Ubuntu 24.04 .deb), macOS .app.
- [ ] Results cache (path + size + mtime) so re-opening big folders is instant.
- [ ] Column chooser / saved column layout; remember sort.
- [ ] Possible extra formats: Ogg Vorbis/Opus, M4A (AAC/ALAC), WAV.
- [x] BDInfo engine (UniqProject/BDInfo 0.7.6.2, LGPL 2.1) in external/BDInfo as its own BDInfo.dll (kept outside
      the single-file ces.exe, LICENSE-BDInfo.txt in the packages); reads BD folders, ISOs and ISOs inside RAR sets;
      `ces bdinfo` (2026-11-17).
  - [x] Compare: BDInfo tab, the in-depth option: quick read on add, Full scan of each compared title, quick
        summaries side by side with differences highlighted, full reports (copy / save), in the HTML report.
  - [ ] Spot: video formats too, at a quick glance: MKV/MP4/M2TS/BD/DVD in the list with codec, profile, resolution,
        HDR, bitrate, encoder library + settings (x264/x265 info string), audio + subtitle tracks; details window.
        BD folders/ISOs via BDInfo's quick read; MKV/MP4 need ffprobe or MediaInfo (Spot needs no ffmpeg today).
  - [ ] A built-in Blu-ray test fixture (tiny BD authored with tsMuxeR); real-disc test runs with CES_BD_SAMPLE.
- [x] Light mode and Follow system (switches live with the OS theme), 2026-11-18: Light and Dark define the same 97
      keys in Theme/Colors.axaml (Fluent overrides incl. buttons, toggles, text/combo boxes, check/radio, scrollbars,
      expander, menus, tooltips, progress); no hard-coded UI colours left in code (ThemeResources.Themed binds code-built
      controls, custom-drawn charts repaint on ActualThemeVariantChanged); title bar, message boxes and About follow the
      theme. CES_THEME=Light|Dark|System overrides the setting for one run. Dark is pixel-identical to 0.3.1 (tour diff).
      The HTML report stays dark.
- [x] Published on GitHub as Taoism/CES (v0.3.0 2026-11-16, v0.3.1 2026-11-17)
