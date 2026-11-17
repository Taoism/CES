# CES TODO

CES = NothingElseCompares2u (Compare) + EncoderSpot (Spot), merged 2026-11-16 as v0.3.0 (local only).

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
- [ ] Publish CES on GitHub when the user says so (NEC2u repo to be deleted by the user)
