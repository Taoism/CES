namespace NothingElseCompares2u.Cli;

internal static class Help
{
    public const string Text = """
        nec2u — NothingElseCompares2u command line

        Usage: nec2u <command> <source> [<source> …] [options]

        A source is a media file (mkv, mp4, m2ts, …), a Blu-ray folder (with BDMV), a DVD folder
        (VIDEO_TS) or an ISO image. Pick a disc title with path@00800.mpls or path@3 (see info --titles).
        The first source is the reference; others are compared against it.

        Commands
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
                      --out DIR        output folder (default ./nec2u-shots)
                      --auto-sync      detect offsets first
                      --no-aspect      keep anamorphic pixels (no DAR correction)
                      --tonemap on|off|auto   HDR → SDR (default auto)
                      --deinterlace    bwdif before grabbing
                      --height H       scale all shots to H pixels high
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
          report    HTML report with specs, findings, screenshots (and metrics with --metrics)
                      --out DIR  --title TEXT  -n N  --no-video  --no-audio  --no-sync

        Common options
          --offset [I=]T     source I (default 2) starts T seconds later than the reference
          --ffmpeg DIR       folder with ffmpeg/ffprobe     --mediainfo PATH
          --verbose          print every ffmpeg command
        """;
}
