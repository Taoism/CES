using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Controls;
using NothingElseCompares2u.Model;

namespace NothingElseCompares2u;

public sealed partial class MainWindow
{
    // video
    private readonly NumericUpDown _videoPoints = new() { Minimum = 2, Maximum = 60, Value = 8, FormatString = "0", Width = 110 };
    private readonly CheckBox _videoNative = new() { Content = "Native resolution test", IsChecked = true };
    private readonly CheckBox _videoDeint = new() { Content = "Deinterlace" };
    private readonly StackPanel _videoFindings = new() { Spacing = 8 };
    private readonly LineChart _nativeChart = new() { Title = "Native resolution: descale error per height (a notch = upscaled from there)", Height = 230 };
    private readonly LineChart _specChartH = new() { Title = "Horizontal detail spectrum", Height = 210 };
    private readonly LineChart _specChartV = new() { Title = "Vertical detail spectrum", Height = 210 };

    // audio
    private readonly StackPanel _audioSetup = new() { Spacing = 6 };
    private readonly Dictionary<int, ComboBox> _audioTrackPick = [];
    private readonly CheckBox _audioFull = new() { Content = "Whole title (slow)" };
    private readonly StackPanel _audioFindings = new() { Spacing = 8 };
    private readonly LineChart _audioSpectrum = new() { Title = "Average spectrum (first analysed track per source)", Height = 240 };
    private readonly StackPanel _spectrograms = new() { Spacing = 8 };

    private Control BuildVideoTab()
    {
        _videoPoints.Value = _settings.VideoPoints;
        Avalonia.Automation.AutomationProperties.SetName(_videoPoints, "Sample frames");
        _videoNative.IsChecked = _settings.NativeResolution;
        ToolTip.SetTip(_videoNative, "Descales the most detailed frames to many heights; an upscale rebuilds almost perfectly at the height it came from.");
        var run = new Button { Content = "Analyse video", Classes = { "accent" } };
        run.Click += async (_, _) => await RunVideoAnalysisAsync();
        _busyDisabled.Add(run);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        bar.Children.AddRange([Muted("Sample frames", 13), _videoPoints, _videoNative, _videoDeint, run,
            Muted("Bit depth (padded 8-bit), upscales, sharpness, black bars, HDR brightness, interlacing.")]);
        foreach (var c in bar.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;

        _nativeChart.XFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + "p";
        _nativeChart.YFormat = v => v.ToString("0.00", CultureInfo.InvariantCulture);
        foreach (var c in new[] { _specChartH, _specChartV })
        {
            c.XFormat = v => (v * 100).ToString("0", CultureInfo.InvariantCulture) + " % Nyq";
            c.YFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + " dB";
        }
        var charts = new StackPanel { Spacing = 8, Children = { _nativeChart, _specChartH, _specChartV } };
        var content = new StackPanel { Spacing = 12, Children = { _videoFindings, charts } };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = content, Margin = new Thickness(0, 8, 0, 0) });
        RefreshVideoResults();
        return root;
    }

    private async Task RunVideoAnalysisAsync()
    {
        if (_tools == null || _items.Count == 0)
            return;
        _settings.VideoPoints = (int)(_videoPoints.Value ?? 8);
        _settings.NativeResolution = _videoNative.IsChecked == true;
        var opt = new VideoAnalysisOptions
        {
            SamplePoints = _settings.VideoPoints,
            NativeResolution = _settings.NativeResolution,
            Deinterlace = _videoDeint.IsChecked == true,
        };
        await RunJobAsync("Video analysis", async ct =>
        {
            var analyzer = new VideoAnalyzer(_tools, _temp);
            for (var i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Title.Info?.Video == null)
                    continue;
                var idx = i;
                var prog = new Progress<string>(s => SetStatus($"[{idx + 1}] {it.Name}: {s}"));
                SetProgress((double)i / _items.Count);
                it.Video = await analyzer.AnalyzeAsync(it.Title, it.Offset, opt, prog, ct);
                RefreshVideoResults();
            }
        });
        RefreshVideoResults();
        RefreshSpecs();
    }

    private void RefreshVideoResults()
    {
        _videoFindings.Children.Clear();
        var any = false;
        for (var i = 0; i < _items.Count; i++)
        {
            var rep = _items[i].Video;
            if (rep == null)
                continue;
            any = true;
            _videoFindings.Children.Add(CrossCard(i, _items[i].Name, rep.Findings, i > 0 ? CrossFindings.Video(_items[0], _items[i]) : []));
        }
        if (!any)
            _videoFindings.Children.Add(Muted("Press “Analyse video”: frames are sampled across the timeline of each source (with its offset).", 13));

        _nativeChart.SetSeries(_items.Select((it, i) => (it, i)).Where(x => x.it.Video?.NativeRes is { Curve.Count: > 0 }).Select(x =>
            new ChartSeries($"{x.i + 1}", Palette[x.i % Palette.Length],
                x.it.Video!.NativeRes!.Curve.Select(p => (double)p.Height).ToArray(),
                x.it.Video.NativeRes.Curve.Select(p => p.Error * 1000).ToArray())));
        _specChartH.SetSeries(SpectrumSeries(f => f.Spectrum.Horizontal));
        _specChartV.SetSeries(SpectrumSeries(f => f.Spectrum.Vertical));
    }

    private IEnumerable<ChartSeries> SpectrumSeries(Func<VideoFrameResult, double[]> pick)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            var rep = _items[i].Video;
            if (rep == null || rep.Frames.Count == 0)
                continue;
            var arrays = rep.Frames.Select(pick).Where(a => a.Length > 0).ToList();
            if (arrays.Count == 0)
                continue;
            var n = arrays.Min(a => a.Length);
            var y = new double[n];
            for (var k = 0; k < n; k++)
                y[k] = 10 * Math.Log10(arrays.Average(a => Math.Pow(10, a[k] / 10)));
            var x = Enumerable.Range(0, n).Select(k => (k + 0.5) / n).ToArray();
            // skip DC / lowest bins: they dwarf the rest
            yield return new ChartSeries($"{i + 1}", Palette[i % Palette.Length], x.Skip(2).ToArray(), y.Skip(2).ToArray());
        }
    }

    private Border FindingsCard(int index, string name, IEnumerable<Finding> findings)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(ChipLabel(index, name));
        foreach (var f in findings.OrderByDescending(f => f.Level))
        {
            var t = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            t.Inlines!.Add(new Avalonia.Controls.Documents.Run((f.Level == 2 ? "⚠ " : f.Level == 1 ? "• " : "") + f.Area + ": ")
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = f.Level == 2 ? new SolidColorBrush(Color.Parse("#FFE87070")) : f.Level == 1 ? new SolidColorBrush(Color.Parse("#FFE0A040")) : Brush("MutedTextBrush"),
            });
            t.Inlines.Add(new Avalonia.Controls.Documents.Run(f.Text));
            panel.Children.Add(t);
        }
        return new Border { Classes = { "card" }, Child = panel };
    }

    // ---------- audio ----------

    private Control BuildAudioTab()
    {
        _audioFull.IsChecked = _settings.AudioFull;
        ToolTip.SetTip(_audioFull, "Measure loudness and statistics over the whole title instead of 8 windows of 20 s.");
        var run = new Button { Content = "Analyse audio", Classes = { "accent" } };
        run.Click += async (_, _) => await RunAudioAnalysisAsync();
        _busyDisabled.Add(run);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        bar.Children.AddRange([_audioFull, run, Muted("EBU R128 loudness, true peak, per-channel levels, bits used, spectrum cutoff (lossy→lossless, upsampling), spectrogram.")]);
        foreach (var c in bar.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;
        _audioSpectrum.XFormat = v => (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " kHz";
        _audioSpectrum.YFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + " dB";
        var content = new StackPanel { Spacing = 12, Children = { new Border { Classes = { "card" }, Child = _audioSetup }, _audioFindings, _audioSpectrum, _spectrograms } };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = content, Margin = new Thickness(0, 8, 0, 0) });
        RefreshAudioSetup();
        RefreshAudioResults();
        return root;
    }

    private void RefreshAudioSetup()
    {
        _audioSetup.Children.Clear();
        _audioTrackPick.Clear();
        _audioSetup.Children.Add(new TextBlock { Text = "Tracks to analyse", Classes = { "header" } });
        if (_items.Count == 0)
        {
            _audioSetup.Children.Add(Muted("No sources yet."));
            return;
        }
        for (var i = 0; i < _items.Count; i++)
        {
            var tracks = _items[i].Title.Info?.Audio.ToList() ?? [];
            var choices = new List<string> { "All tracks" };
            choices.AddRange(tracks.Select((t, k) => $"{k + 1}: {t.Label}"));
            var combo = new ComboBox { ItemsSource = choices, SelectedIndex = tracks.Count > 0 ? 1 : 0, MinWidth = 420, IsEnabled = tracks.Count > 0 };
            _audioTrackPick[i] = combo;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 10 };
            var label = ChipLabel(i, _items[i].Name);
            row.Children.Add(label);
            Grid.SetColumn(combo, 1);
            row.Children.Add(tracks.Count > 0 ? combo : Muted("no audio"));
            if (tracks.Count == 0)
                Grid.SetColumn(row.Children[^1], 1);
            _audioSetup.Children.Add(row);
        }
    }

    private async Task RunAudioAnalysisAsync()
    {
        if (_tools == null || _items.Count == 0)
            return;
        _settings.AudioFull = _audioFull.IsChecked == true;
        var opt = new AudioAnalysisOptions { FullLength = _settings.AudioFull };
        var jobs = new List<(int Item, int Track)>();
        for (var i = 0; i < _items.Count; i++)
        {
            var count = _items[i].Title.Info?.Audio.Count() ?? 0;
            if (count == 0 || !_audioTrackPick.TryGetValue(i, out var combo))
                continue;
            if (combo.SelectedIndex <= 0)
                jobs.AddRange(Enumerable.Range(0, count).Select(t => (i, t)));
            else
                jobs.Add((i, combo.SelectedIndex - 1));
        }
        if (jobs.Count == 0)
        {
            await Dialogs.MessageBox.ShowAsync(this, "None of the sources has audio.", Dialogs.MessageKind.Info);
            return;
        }
        foreach (var it in _items)
            it.Audio.Clear();
        await RunJobAsync("Audio analysis", async ct =>
        {
            var analyzer = new AudioAnalyzer(_tools, _temp);
            var n = 0;
            foreach (var (i, t) in jobs)
            {
                var it = _items[i];
                var prog = new Progress<string>(s => SetStatus($"[{i + 1}] {it.Name} · track {t + 1}: {s}"));
                SetProgress((double)n++ / jobs.Count);
                it.Audio.Add(await analyzer.AnalyzeAsync(it.Title, t, it.Offset, opt, prog, ct));
                RefreshAudioResults();
            }
        });
        RefreshAudioResults();
        RefreshSpecs();
    }

    private void RefreshAudioResults()
    {
        _audioFindings.Children.Clear();
        _spectrograms.Children.Clear();
        var series = new List<ChartSeries>();
        for (var i = 0; i < _items.Count; i++)
        {
            foreach (var rep in _items[i].Audio)
            {
                var first = _items[i].Audio.FirstOrDefault() == rep;
                var card = CrossCard(i, $"{_items[i].Name} · track {rep.TrackIndex + 1}: {rep.Stream.Label}", rep.Findings, i > 0 && first ? CrossFindings.Audio(_items[0], _items[i]) : []);
                if (card.Child is StackPanel sp && rep.Channels.Count > 0)
                {
                    var table = new Grid { ColumnDefinitions = new ColumnDefinitions("60,110,110,110,110,*"), Margin = new Thickness(0, 6, 0, 0) };
                    string[] head = ["Ch", "Peak", "RMS", "Noise floor", "Crest", "Bits used"];
                    table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    for (var c = 0; c < head.Length; c++)
                    {
                        var h = new TextBlock { Text = head[c], Classes = { "muted" }, FontSize = 12 };
                        Grid.SetColumn(h, c);
                        table.Children.Add(h);
                    }
                    var lossy = Probe.MediaProber.IsLossy(rep.Stream.Codec) && !(rep.Stream.Codec == "dts" && rep.Stream.Profile.Contains("MA", StringComparison.Ordinal));
                    var r = 1;
                    foreach (var ch in rep.Channels)
                    {
                        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        string[] vals = [ch.Name, Format.Db(ch.PeakDb), Format.Db(ch.RmsDb), Format.Db(ch.NoiseFloorDb), Format.Db(ch.CrestDb), lossy ? "n/a (lossy)" : ch.BitsUsedText];
                        for (var c = 0; c < vals.Length; c++)
                        {
                            var tb = new TextBlock { Text = vals[c], FontSize = 12 };
                            Grid.SetRow(tb, r);
                            Grid.SetColumn(tb, c);
                            table.Children.Add(tb);
                        }
                        r++;
                    }
                    sp.Children.Add(table);
                }
                _audioFindings.Children.Add(card);

                if (rep.Spectrum.Length > 0 && series.All(s => s.Name != $"{i + 1}"))
                {
                    var n = rep.Spectrum.Length;
                    var hz = Enumerable.Range(0, n).Select(k => k * (rep.SpectrumRate / 2.0) / (n - 1)).ToArray();
                    // light thinning keeps the chart responsive
                    var step = Math.Max(1, n / 1500);
                    series.Add(new ChartSeries($"{i + 1}", Palette[i % Palette.Length],
                        hz.Where((_, k) => k % step == 0).ToArray(), rep.Spectrum.Where((_, k) => k % step == 0).ToArray()));
                }
                if (TryLoadBitmap(rep.SpectrogramPng) is { } bmp)
                {
                    _spectrograms.Children.Add(ChipLabel(i, $"{_items[i].Name} · track {rep.TrackIndex + 1} · spectrogram, 60 s from the middle"));
                    _spectrograms.Children.Add(new Image { Source = bmp, Stretch = Stretch.Uniform, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Left });
                }
            }
        }
        if (_audioFindings.Children.Count == 0)
            _audioFindings.Children.Add(Muted("Pick tracks above and press “Analyse audio”.", 13));
        _audioSpectrum.YMin = -140;
        _audioSpectrum.SetSeries(series);
    }
}
