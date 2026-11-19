# CES TODO

CES = Compare + Spot modes, v0.3.3 (2026-11-18).

Done: whole-title sync, PAL speed, edits, sessions, ranking, difference/flicker, detailed shots (2026-11-15);
artefacts (grain/banding/halos/chroma), audio remaster/upmix checks, HDR10+/DV metadata, subtitles (burn-in, timing),
slow.pics + BBCode/Markdown, screenshot cache / hardware decoding, grade & framing (remasters), still images (2026-11-16);
grade + frame checks saved in sessions, DVD subtitle timing, frame-exact cuts, telecine (IVTC) pairing, metrics
first-frame alignment, dropped/duplicated frames, grade over time, dirt/speck and stabilisation checks, README
screenshots from synthetic sources (2026-11-16).

Open:
- [ ] slow.pics upload not tested live (publishes images) — try once with the user's OK
- [x] Dolby Vision on real files (Jellyfin test clips, profiles 5 / 8.1 / 8.4, 2026-11-18): RPU read for every profile,
      mastering-only RPU (ffprobe prints no level-1 blocks) not mistaken for per-shot metadata, tidy HDR line.
  - [x] Per-shot DV values (2026-11-18): own RPU parser (Analysis/DoviRpu.cs: mapping, NLQ, DM data, extension
        blocks L1 / L2 / L6), RPU copied (not decoded) from the keyframe, frame picked by FFmpeg's -debug_ts packet
        times; trusted only when its source max equals FFmpeg's. The Jellyfin clips do carry L1 (the "mastering only"
        verdict was ffprobe not printing it): 8.1 / 8.4 follow the picture, P5's L1 is constant. Trims (L2) listed.
        Not covered: AV1 (profile 10) RPUs, use_prev_vdr_rpu, the CM v4.0 second block set (L3/L8–L11).
- [x] Chroma-origin check under lossy re-encodes (2026-11-18): "probably upsampled" hint between -8 and -4 dB, tested
      to CRF 35 (coherence / local-energy alternatives tried: codec noise correlates with luma edges too). Real camera
      4:4:4 (Xiph SVT park_joy / old_town_cross): genuine reads -1…+7 dB (no false alarms), but upsampled real footage
      only -0…-7 dB, so many upsampled+re-encoded real files are not caught.
- [x] Sync rescue (2026-11-18): when the audio does not match (other mix, delayed-video remux) the picture alone finds
      the offset (candidates from the audio + a grid near zero, best agreed frame match; integration test).
- [x] Inverse telecine on real material (2026-11-18): Night of the Living Dead, public-domain NTSC DVD (film by
      repeated frames, 1 in 5) vs a 1080p 23.976 release: frame-repeat pulldown detected, a different and shorter cut
      mapped in 16 sections (weak but agreeing audio probes count), screenshots land on the same frames.
  - [ ] No frame-exact cuts on that pair yet (picture cut refinement needs work for very different transfers).
- [x] Release cards with value (share of the best score at share of its size, VMAF per GB), timeline strip under the
      viewer, Shift+drag region zoom across sources (2026-11-18).
- [ ] Calibrate the untagged-MP3 heuristics (Xing old/new, Blade, FhG, GOGO...) against the original
      EncSpot 2.2 on the same files (reference/orig has the old installers) and against mp3guessenc output.
- [~] Real encoder samples (2026-11-18, RareWares + Doom9 builds in testmedia-ext/encoders): GOGO 3.13 (VBR by "GOGO"
      in the Xing quality field; CBR untagged → low "GOGO or old LAME"), Helix hmp3 5.2.4 (LAME-style tag "H5.24"),
      LAME 3.95.1 and 3.99.5o, fpMP3 (LAME 3.98r; writes a truncated last frame), tooLAME / TwoLAME MP2 (still
      Unknown). Then (RareWares retro): Blade 0.94.2 (fingerprint: reservoir unused, simple stereo, no scfsi), FhG
      FastEnc 1.02 (no scfsi, heavy preflag, zero ancillary; VBR via VBRI), LAME 3.80 / 3.88 (pre-3.90 tags have no
      extended fields or CRC) / 3.90 / 3.93.1 — all 25 MP3 samples identified. FhG ACM professional 3.4 (acmenc 0.4 — needs no MSVCR71; codec registered as WOW64 msacm.l3codecp; the
      front end adds an Info header, the stream fingerprint still says FhG), iTunes 12.13.12.3 (COM ConvertFile2:
      named from ID3v2.2 TEN + iTunSMPB delay/padding; untagged: no scfsi and no preflag at all), Xing 1.5 (installed into testmedia-ext; its x3enc CLI: CBR = "Xing (old)" by no short blocks, VBR by its Xing
      header, whose frame count includes the header frame itself — no longer reported), FhG MP3 Surround 1.5 (licence accepted
      with the user's OK): CBR confirmed by the same Fraunhofer fingerprint (VBR modes fail silently). Still missing:
      FhG mp3enc 3.1 (skipped for now: paid encoder that waits for a licence key; the RareWares copy is a warez rip), DOS-only l3enc / LAME 3.97d, a fingerprint for MP2
      (tooLAME / TwoLAME).
- [x] Spot: lossless-source check for FLAC and WAV (2026-11-18): lossy-source brick wall, upsampling cliff at
      22.05/24 kHz, padded bits; Source column, Spectrum tab, CSV.
- [ ] Packaging like TaoExif: Inno installer + win zip (bundled ffmpeg full + MediaInfo, updater), .deb, linux
      tarball (done: Debian 13 + Ubuntu 24.04 .deb), macOS .app.
- [x] Results cache (path + size + mtime), 2026-11-18: Spot file results and Compare results (video/audio analysis,
      sync, remaster checks, metrics, BDInfo full scans) come back when a file is seen again; LRU size limit in Settings.
      Saved Spot results (.cess), Save details (text / JSON), Compare tab Copy / Save… as text, File › Save results
      (Ctrl+S, per mode) / Open saved results (Ctrl+L), always-visible job area + Stop at the bottom right.
- [x] Column chooser (header right-click, Spot › Columns), saved widths/order/sort (2026-11-18).
- [x] Extra formats (2026-11-18): WAV, Ogg Vorbis/Opus/FLAC (page CRCs), M4A AAC/ALAC, AC-3/E-AC-3 in MP4 incl.
      Dolby Atmos (JOC) — checked on 156 real Apple Music Atmos tracks.
- [x] BDInfo engine (UniqProject/BDInfo 0.7.6.2, LGPL 2.1) in external/BDInfo as its own BDInfo.dll (kept outside
      the single-file ces.exe, LICENSE-BDInfo.txt in the packages); reads BD folders, ISOs and ISOs inside RAR sets;
      `ces bdinfo` (2026-11-17).
  - [x] Compare: BDInfo tab, the in-depth option: quick read on add, Full scan of each compared title, quick
        summaries side by side with differences highlighted, full reports (copy / save), in the HTML report.
  - [x] Spot: video formats too (2026-11-18, via ffprobe/MediaInfo; ISOs, disc folders, video in RAR sets), at a quick glance: MKV/MP4/M2TS/BD/DVD in the list with codec, profile, resolution,
        HDR, bitrate, encoder library + settings (x264/x265 info string), audio + subtitle tracks; details window.
        BD folders/ISOs via BDInfo's quick read; MKV/MP4 need ffprobe or MediaInfo (Spot needs no ffmpeg today).
  - [ ] A built-in Blu-ray test fixture (tiny BD authored with tsMuxeR); real-disc test runs with CES_BD_SAMPLE.
- [x] Light mode and Follow system (switches live with the OS theme), 2026-11-18: Light and Dark define the same 97
      keys in Theme/Colors.axaml (Fluent overrides incl. buttons, toggles, text/combo boxes, check/radio, scrollbars,
      expander, menus, tooltips, progress); no hard-coded UI colours left in code (ThemeResources.Themed binds code-built
      controls, custom-drawn charts repaint on ActualThemeVariantChanged); title bar, message boxes and About follow the
      theme. CES_THEME=Light|Dark|System overrides the setting for one run. Dark is pixel-identical to 0.3.1 (tour diff).
      The HTML report stays dark.
- [x] Published on GitHub as Taoism/CES (v0.3.0 2026-11-16, v0.3.1 2026-11-17, v0.3.2 2026-11-18, v0.3.3 2026-11-18)
