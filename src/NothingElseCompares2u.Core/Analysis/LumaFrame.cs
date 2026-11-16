using NothingElseCompares2u.Model;
using NothingElseCompares2u.Tools;

namespace NothingElseCompares2u.Analysis;

/// <summary>The luma plane of one decoded frame at its native bit depth (no conversion, no scaling).</summary>
public sealed class LumaFrame
{
    public required ushort[] Data { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitDepth { get; init; }
    public double Time { get; init; }

    public int MaxCode => (1 << BitDepth) - 1;

    /// <summary>Values scaled to 0…1 by the full code range.</summary>
    public float[] ToFloat()
    {
        var f = new float[Data.Length];
        var s = 1f / MaxCode;
        for (var i = 0; i < f.Length; i++)
            f[i] = Data[i] * s;
        return f;
    }

    /// <param name="plane">"y" for luma, "u"/"v" for a chroma plane (at its own, possibly subsampled, size).</param>
    public static async Task<LumaFrame?> GrabAsync(FfTools tools, MediaTitle title, double time, string tempDir, bool deinterlace, CancellationToken ct, string plane = "y")
    {
        var v = title.Info?.Video;
        if (v == null || v.Width <= 0 || v.Height <= 0)
            return null;
        var (w, h) = plane == "y" ? (v.Width, v.Height) : v.ChromaSubsampling switch
        {
            "4:2:0" => ((v.Width + 1) / 2, (v.Height + 1) / 2),
            "4:2:2" => ((v.Width + 1) / 2, v.Height),
            _ => (v.Width, v.Height),
        };
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error" };
        var input = InputBuilder.Input(title, time, 2, tempDir);
        args.AddRange(input.Args);
        var vf = InputBuilder.VideoPrefix(title) + input.TrimFilter + (deinterlace ? "bwdif=mode=send_frame:deint=all," : "") + (v.ChromaSubsampling == "RGB" ? (v.BitDepth > 8 ? "format=gray16le" : "format=gray") : $"extractplanes={plane}");
        args.AddRange(["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", vf, "-frames:v", "1", "-f", "rawvideo", "-"]);
        var res = await ProcessRunner.RunAsync(tools.FFmpeg, args, ct).ConfigureAwait(false);
        var bytes = res.StdOut;
        var pixels = w * h;
        if (bytes.Length == pixels)
        {
            var d = new ushort[pixels];
            for (var i = 0; i < pixels; i++)
                d[i] = bytes[i];
            return new LumaFrame { Data = d, Width = w, Height = h, BitDepth = 8, Time = time };
        }
        if (bytes.Length == pixels * 2)
        {
            var d = new ushort[pixels];
            for (var i = 0; i < pixels; i++)
                d[i] = (ushort)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
            var depth = v.ChromaSubsampling == "RGB" ? 16 : v.BitDepth is > 8 and <= 16 ? v.BitDepth : 10;
            return new LumaFrame { Data = d, Width = w, Height = h, BitDepth = depth, Time = time };
        }
        return null;
    }
}
