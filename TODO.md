# TODO

Done: whole-title sync, PAL speed, edits, sessions, ranking, difference/flicker, detailed shots (2026-11-15);
artefacts (grain/banding/halos/chroma), audio remaster/upmix checks, HDR10+/DV metadata, subtitles (burn-in, timing),
slow.pics + BBCode/Markdown, screenshot cache / hardware decoding, grade & framing (remasters), still images (2026-11-16).

Open:
- [ ] slow.pics upload not tested live (publishes images) — try once with the user's OK
- [ ] Dolby Vision metadata parsing untested on a real DV file (none available)
- [ ] Chroma-origin check is hidden by lossy re-encodes (works on clean masters only)
- [ ] Subtitle timing for DVD titles (VOB subpictures)
- [ ] Grade results are not saved in .nec2u sessions yet (re-run on open)
- [ ] Frame-exact cut points (now ~1–2 s), telecine/pulldown pairing (29.97 vs 23.976)
- [ ] Release cards / per-GB value view, timeline strip, region zoom across sources
- [ ] Packaging like TaoExif (installer, bundled ffmpeg full + MediaInfo, updater, .deb, macOS)
