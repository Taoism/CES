using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Ces.Controls;

namespace Ces;

/// <summary>
/// Scripted walk through the tabs that renders each one to a PNG (README screenshots). Started with the
/// CES_TOUR environment variable set to the output folder and the sources on the command line; never touches the
/// mouse or keyboard, opens on a secondary screen when there is one, and leaves the user's settings alone.
/// </summary>
public sealed partial class MainWindow
{
    private bool _tour;

    private async Task RunTourAsync(string outDir)
    {
        _tour = true;
        Directory.CreateDirectory(outDir);
        if (Screens.All.FirstOrDefault(s => !s.IsPrimary) is { } other)
        {
            WindowState = WindowState.Normal;
            Position = new PixelPoint(other.WorkingArea.X + 40, other.WorkingArea.Y + 40);
        }
        Width = 1480;
        Height = 900;

        if (_items.Skip(1).Any(i => !i.Synced))
            await AutoSyncAsync(true, true);

        _shotMode.SelectedIndex = 0;
        _shotCount.Value = 6;
        await TakeShotsAsync(true);
        SelectRow(2);
        SetViewMode(CompareMode.Wipe);
        await SnapAsync(outDir, "screenshots-wipe", 0);
        SetViewMode(CompareMode.SideBySide);
        await SnapAsync(outDir, "screenshots-side-by-side", 0);
        SetViewMode(CompareMode.Difference);
        await SnapAsync(outDir, "screenshots-difference", 0);
        SetViewMode(CompareMode.Wipe);

        await SnapAsync(outDir, "specs", 1);
        _tabs.SelectedIndex = 2;
        await RunVideoAnalysisAsync();
        await SnapAsync(outDir, "video-analysis", 2);
        _tabs.SelectedIndex = 3;
        await RunAudioAnalysisAsync();
        await SnapAsync(outDir, "audio-analysis", 3);
        _tabs.SelectedIndex = 4;
        await RunMetricsAsync();
        await SnapAsync(outDir, "quality-metrics", 4);
        _tabs.SelectedIndex = 5;
        await RunGradeAsync();
        await SnapAsync(outDir, "remaster", 5);
        Close();
    }

    private void SelectRow(int index)
    {
        if (index < _rows.Count)
            _rowList.SelectedIndex = index;
    }

    private async Task SnapAsync(string outDir, string name, int tab)
    {
        _tabs.SelectedIndex = tab;
        // let layout, image decoding and charts settle
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, CancellationToken.None);
            await Task.Delay(400, CancellationToken.None);
        }
        var scale = RenderScaling;
        var size = new PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bmp = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bmp.Render(this);
        bmp.Save(Path.Combine(outDir, name + ".png"), PngBitmapEncoderOptions.Default);
        // the tab as text too (what Copy / Save… on the tab strip produce)
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".txt"), TabText(), CancellationToken.None);
    }
}
