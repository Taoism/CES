# TODO

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
- [ ] Packaging like TaoExif (installer, bundled ffmpeg full + MediaInfo, updater, .deb, macOS)
