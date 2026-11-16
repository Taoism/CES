# TODO

Done 2026-11-15: whole-title sync, PAL speed, different edits (sections), sessions, release ranking, difference/flicker
views, detailed-shot screenshots.

Next (agreed list, items 8–14):
- [ ] 8. More artefact detection: banding, over-denoising (grain loss), sharpening halos, chroma subsampling origin
- [ ] 9. Dolby Vision / HDR10+ dynamic metadata vs measured brightness (bad DV P5→P8, fake HDR10+)
- [ ] 10. Audio between releases: loudness delta, re-master / brick-wall limiting, upmixed 5.1 detection
- [ ] 11. Subtitles: burn into screenshots, compare subtitle timing between releases
- [ ] 12. Packaging like TaoExif: installer, bundled ffmpeg full build + MediaInfo, self-updater, .deb, macOS
- [ ] 13. Share: slow.pics upload, BBCode/Markdown summary for forums
- [ ] 14. Speed: hardware decoding for 4K HEVC, decoded-frame cache

Smaller:
- [ ] Cut points are located to ~1–2 s (3 s audio snippets); could be refined to the frame with the picture
- [ ] Native save dialog can't be driven by UI Automation in tests (session save tested by a round-trip unit test)
- [ ] Physical drives / encrypted discs — out of scope
