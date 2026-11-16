using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Dsp;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u.Tests;

public class SessionTests
{
    [Fact]
    public async Task RoundTripsAnalysisResults()
    {
        var video = new VideoReport { Width = 1920, Height = 1080, BitDepth = 10, Crop = new CropBox(0, 140, 0, 140) };
        video.Frames.Add(new VideoFrameResult
        {
            Time = 12.5,
            Levels = new LevelStats { Min = 64, Max = 940, EffectiveBits = 8, PaddedShare = 0.99 },
            Sharpness = 42,
            Spectrum = new SpectrumStats { Horizontal = [1, 2, 3], CutoffHeight = double.NaN },
            Crop = new CropBox(0, 140, 0, 140),
            PeakNits = double.NaN,
        });
        video.NativeRes = new NativeResResult { NativeHeight = 720, Kernel = Kernel.Bicubic, Confidence = 0.3 };
        video.NativeRes.Curve.Add(new NativeResPoint(720, 0.001, Kernel.Bicubic));
        video.Findings.Add(new Finding { Area = "Upscale", Text = "from 720p", Level = 2 });

        var audio = new AudioReport { TrackIndex = 1, Stream = new StreamInfo { Codec = "flac", Channels = 2 }, IntegratedLufs = -18.1, CutoffHz = 16000, Spectrum = [-10, -20] };
        audio.Channels.Add(new ChannelStats { Name = "L", PeakDb = -1, RmsDb = double.NegativeInfinity, BitDepth = "16/24" });

        var metric = new MetricResult { Start = 60, FrameRate = 23.976, Frames = 2 };
        metric.Series[Metric.Vmaf] = new MetricSeries { Name = "VMAF" };
        metric.Series[Metric.Vmaf].Values.AddRange([95.1, 90.2]);
        metric.FrameTimes.AddRange([60, 60.0417]);

        var s = new SessionFile();
        s.Sources.Add(new SessionSource
        {
            Path = @"C:\x\a.mkv", Title = "a.mkv", Offset = -5.005, Speed = 0.95904, Synced = true,
            Sections = [new SyncSection { Start = 0, Offset = -0.25 }, new SyncSection { Start = 61, Offset = null }],
            Video = video, Audio = [audio], Spectrograms = { [1] = "AAAA" },
        });
        s.Rows.Add(new SessionRow { Time = 21.016, Caption = "detailed shot" });
        s.Metrics.Add(new SessionMetric { Reference = 0, Distorted = 1, Result = metric });
        s.Ranking.Add(new SessionRank { Item = 1, Rank = 1 });

        var path = Path.Combine(Path.GetTempPath(), $"nec2u-test-{Guid.NewGuid():N}.nec2u");
        try
        {
            await s.SaveAsync(path);
            var back = await SessionFile.LoadAsync(path);
            var src = back.Sources.Single();
            Assert.Equal(-5.005, src.Offset);
            Assert.Equal(0.95904, src.Speed);
            Assert.Null(src.Sections[1].Offset);
            Assert.Equal(720, src.Video!.NativeRes!.NativeHeight);
            Assert.Equal(Kernel.Bicubic, src.Video.NativeRes.Curve.Single().Kernel);
            Assert.Equal(140, src.Video.Crop!.Top);
            Assert.True(double.IsNaN(src.Video.Frames[0].PeakNits));
            Assert.Equal(8, src.Video.Frames[0].Levels.EffectiveBits);
            Assert.Equal("from 720p", src.Video.Findings.Single().Text);
            Assert.True(double.IsNegativeInfinity(src.Audio[0].Channels[0].RmsDb));
            Assert.Equal("16 of 24", src.Audio[0].Channels[0].BitsUsedText);
            Assert.Equal("AAAA", src.Spectrograms[1]);
            var m = back.Metrics.Single().Result!;
            Assert.Equal([95.1, 90.2], m.Series[Metric.Vmaf].Values);
            Assert.Equal(60.0417, m.TimeOf(1), 6);
            Assert.Equal(21.016, back.Rows.Single().Time);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
