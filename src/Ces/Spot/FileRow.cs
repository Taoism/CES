using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Ces.Spot;

namespace Ces.SpotUi;

/// <summary>One file in the list; filled in when its analysis finishes.</summary>
public sealed class FileRow(string path) : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private AnalysisResult? _result;
    private bool _busy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; } = path;
    /// <summary>File name; for a file inside a RAR set "set.rar › track.flac".</summary>
    public string Name { get; } = Vfs.Vfs.IsVirtual(path) ? Vfs.Vfs.Display(path) : System.IO.Path.GetFileName(path);
    public string Folder { get; } = System.IO.Path.GetDirectoryName(Vfs.Vfs.RealFile(path)) ?? "";
    public long Size { get; } = SafeLength(path);
    public string SizeText => Size.ToString("N0", Inv);

    public AnalysisResult? Result
    {
        get => _result;
        set
        {
            _result = value;
            _busy = false;
            Raise(string.Empty); // every column changes
        }
    }

    public bool IsBusy
    {
        get => _busy;
        set
        {
            _busy = value;
            Raise(nameof(Encoder));
        }
    }

    public bool IsDone => _result is not null;
    public string Format => _result?.Format ?? "";
    public string Encoder => _result is null ? (_busy ? "analyzing…" : "") : _result.Error is not null ? "error: " + _result.Error : _result.Encoder;
    public string Settings => _result?.EncoderSettings ?? "";
    public double Bitrate => _result?.BitrateKbps ?? 0;
    public string BitrateText => _result is null || _result.Error is not null ? "" : string.Create(Inv, $"{_result.BitrateKbps:0}");
    public double Seconds => _result?.DurationSeconds ?? 0;
    public string LengthText => _result is null || _result.Error is not null ? "" : Fmt.Duration(_result.DurationSeconds);
    public int SampleRate => _result?.SampleRate ?? 0;
    public string SampleRateText => SampleRate == 0 ? "" : SampleRate.ToString(Inv);
    public string ModeText => _result is null ? "" : _result.BitsPerSample > 0 ? string.Create(Inv, $"{_result.BitsPerSample}-bit {_result.ModeName}") : _result.ModeName;
    public string VbrText => _result is null || _result.Error is not null || _result.Format == "FLAC" ? "" : _result.IsVbr ? "yes" : "no";
    public long Frames => _result?.FrameCount ?? 0;
    public string FramesText => Frames == 0 ? "" : Frames.ToString(Inv);
    public string Confidence => _result is null || _result.Error is not null ? "" : _result.Confidence.ToString();
    public QualityRating Quality => _result?.Error is not null ? QualityRating.Poor : _result?.Quality ?? QualityRating.Unknown;
    public int QualitySort => (int)Quality;
    public string QualityText => _result is null ? "" : Quality switch
    {
        QualityRating.Lossless => "lossless",
        QualityRating.Good => "good",
        QualityRating.Fair => "fair",
        QualityRating.Poor => "poor",
        _ => "",
    };
    public bool IsGood => Quality == QualityRating.Good;
    public bool IsFair => Quality == QualityRating.Fair;
    public bool IsPoor => _result is not null && Quality == QualityRating.Poor;
    public bool IsLossless => Quality == QualityRating.Lossless;
    public bool HasProblems => _result is not null && (_result.Problems.Count > 0 || _result.Error is not null);
    public string Problems => _result is null ? "" : _result.Error ?? string.Join("; ", _result.Problems);
    public string Artist => _result?.Artist ?? "";
    public string Title => _result?.Title ?? "";
    public string Album => _result?.Album ?? "";

    private static long SafeLength(string path)
    {
        try
        {
            return Vfs.Vfs.Length(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
