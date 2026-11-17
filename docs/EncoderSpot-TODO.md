# EncoderSpot TODO

- [ ] Calibrate the untagged-MP3 heuristics (Xing old/new, Blade, FhG, GOGO...) against the original
      EncSpot 2.2 on the same files (reference/orig has the old installers) and against mp3guessenc output.
- [ ] More real-world encoder samples: FhG (ACM, fastenc, mp3enc), Xing, Blade, Helix, iTunes, GOGO, old LAME.
- [ ] FLAC: lossy-source / upsampling detection (spectral cutoff, fake 24-bit via wasted bits) + spectrogram tab.
- [ ] Packaging like TaoExif: Inno installer + win zip, .deb (Ubuntu/Debian), linux tarball, macOS .app.
- [ ] Results cache (path + size + mtime) so re-opening big folders is instant.
- [ ] Column chooser / saved column layout; remember sort.
- [ ] Possible extra formats: Ogg Vorbis/Opus, M4A (AAC/ALAC), WAV.
- [ ] License + public GitHub repo (user decision).
