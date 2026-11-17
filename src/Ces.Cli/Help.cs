namespace Ces.Cli;

internal static class Help
{
    public const string Text = """
        ces — CES (Compare Enc Spot) command line

        Usage: ces spot [options] <file or folder>…
               ces bdinfo [options] <Blu-ray>
               ces <command> <source> [<source> …] [options]

        spot      MP3 / FLAC encoder spotting and stream check (no ffmpeg needed)
                    -r recursive  -d details  -j JSON  -q FLAC metadata only  -p only problems  -t N threads
                    exit code 1 when a file has problems; see 'ces spot --help'
        bdinfo    BDInfo report of a Blu-ray folder, ISO or RAR set (no ffmpeg needed)
                    -p NAME playlist(s)  -a all playlists  -q quick (no full scan)  -l list  -o FILE
                    see 'ces bdinfo --help'

        A source is a media file (mkv, mp4, m2ts, …), a Blu-ray folder (with BDMV), a DVD folder
        (VIDEO_TS) or an ISO image. Pick a disc title with path@00800.mpls or path@3 (see info --titles).
        The first source is the reference; others are compared against it.

        Compare commands
          info      Streams, HDR, chapters; side-by-side spec table for several sources
                      --titles         list every title/playlist of a disc
                      --all-playlists  include Blu-ray playlists shorter than a minute
          sync      Find the time offset of each source against the first (audio cross-correlation)
                      --max-shift S    search ±S seconds (default 60)
                      --whole          search the whole title (any offset; decodes all audio)
                      --edits          map a different edit: offsets per section, cut parts
          shots     Screenshots of the same frames in every source
                      -n N             number of evenly spread points (default 6)
                      --at T,T,…       explicit times (1:02:03.5, 3723.5)
                      --random [--seed N] / --chapters
                      --smart          detailed, well-lit shots (keyframe scan of source 1)
                      --out DIR        output folder (default ./ces-shots)
                      --auto-sync      detect offsets first
                      --no-aspect      keep anamorphic pixels (no DAR correction)
                      --tonemap on|off|auto   HDR → SDR (default auto)
                      --deinterlace    bwdif before grabbing
                      --height H       scale all shots to H pixels high
                      --subs LANG|N    burn in subtitles (each source's first track in LANG, or track N)
                      --slowpics       upload the shots to slow.pics (public image host; unlisted unless --public)
                      --summary bbcode|markdown   print a forum summary (with the slow.pics link)
          video     Image analysis: bit depth, upscale/native resolution, sharpness, black bars, HDR nits, interlacing
                      --points N       sampled frames (default 8)   --curve  print the native-res curve
                      --no-native      skip the native resolution test
          audio     Loudness (EBU R128), per-channel stats, used bits, spectrum cutoff (lossy/upsampled check), spectrogram
                      --track N[,N]|all   (default 1)   --full  whole title instead of 8 × 20 s windows
                      --out DIR        save the spectrogram PNGs
          metrics   PSNR / SSIM / VMAF / XPSNR of each source against the first
                      --metrics psnr,ssim,vmaf,xpsnr   --start T   --length T (default 60 s, middle)
                      --subsample N    every N-th frame   --no-crop  no automatic reference crop
                      --auto-sync      detect offsets first
          rank      Which release is best: sync, then VMAF/PSNR/SSIM of each against the reference in
                    several windows across the title; folders after the reference = every release inside
                      --windows N (default 5)   --length T (default 20 s)   --metrics …   --whole / --edits
          grade     What a remaster changed in the picture: framing (zoom/crop/shift, pan & scan) and grade
                    (tone curve, contrast, saturation, colour cast), on matched frames   -n N  --at T,…  --curve
          subs      Subtitle timing of each source against the reference (same language): shift, drift, lines found
                      --subs LANG|N    reference track (default track 1; others use the same language)
          report    HTML report with specs, findings, screenshots (and metrics with --metrics)
                      --out DIR  --title TEXT  -n N  --no-video  --no-audio  --no-sync
                      Blu-ray sources get a BDInfo section (quick read; --bdinfo-scan full scan, --no-bdinfo none)

        Common options
          --offset [I=]T     source I (default 2) starts T seconds later than the reference
          --speed [I=]S      source seconds per reference second: pal (23.976→25), pal24, 0.95904, 24000/25025
          --ffmpeg DIR       folder with ffmpeg/ffprobe     --mediainfo PATH
          --verbose          print every ffmpeg command
          --hw               hardware video decoding (often slower on fast CPUs)
          --no-cache         do not use the screenshot cache
        """;
}
