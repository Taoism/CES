using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Ces.Analysis;
using Ces.Controls;
using Ces.Dialogs;
using Ces.Model;
using Ces.Tools;

namespace Ces;

public sealed partial class MainWindow
{
    private readonly StackPanel _gradeResults = new() { Spacing = 8 };
    private readonly LineChart _toneChart = new() { Title = "Tone curve: brightness (L*) of each source against source 1 at the same percentiles", Height = 280 };
    private readonly LineChart _gradeTimeChart = new() { Title = "Grade over time: midtone ΔL* (solid) and warmth Δb* of each source against source 1, per point", Height = 240 };
    private readonly NumericUpDown _gradePoints = new() { Minimum = 4, Maximum = 40, Value = 12, Increment = 2, Width = 120, FormatString = "0" };
    private readonly CheckBox _gradeFrames = new() { Content = "Frame & restoration checks", IsChecked = true };

    private DockPanel BuildGradeTab()
    {
        var run = new Button { Content = "Compare with source 1", Classes = { "accent" } };
        run.Click += async (_, _) => await RunGradeAsync();
        _busyDisabled.Add(run);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        ToolTip.SetTip(_gradePoints, "Points across the title when there are no screenshots (more points show shot-to-shot grading better)");
        ToolTip.SetTip(_gradeFrames, "Also check dropped/duplicated frames, dirt/specks removed or added, and stabilisation (gate weave) — a few seconds of video at 5 places");
        bar.Children.AddRange([run, new TextBlock { Text = "Points" }, _gradePoints, _gradeFrames,
            Muted("What a remaster or another release changed: framing (zoom/crop, shift, pan & scan), grade (tone curve, contrast, saturation, colour cast) and how it changes by shot, plus dropped frames, dirt removal and stabilisation. Uses the screenshot times, or points across the title. Works for still images too.")]);
        foreach (var c in bar.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;
        _toneChart.XFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + " L*";
        _toneChart.YFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + " L*";
        _gradeTimeChart.XFormat = v => Format.Time(v);
        _gradeTimeChart.YFormat = v => v.ToString("+0;-0;0", CultureInfo.InvariantCulture);
        var content = new StackPanel { Spacing = 12, Children = { _gradeResults, _toneChart, _gradeTimeChart } };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = content, Margin = new Thickness(0, 8, 0, 0) });
        RefreshGradeResults();
        return root;
    }

    private async Task RunGradeAsync()
    {
        if (_tools == null || _items.Count < 2)
        {
            await MessageBox.ShowAsync(this, "Add the reference (source 1) and at least one other source or image.", MessageKind.Info);
            return;
        }
        List<double> times;
        if (_rows.Count > 0)
            times = _rows.Select(r => r.Row.Time).ToList();
        else if (_items.All(i => i.Title.IsStill))
            times = [0];
        else
        {
            var (start, end) = TimelineRange();
            if (double.IsInfinity(end))
                end = start + _items.Where(i => !i.Title.IsStill).Select(i => i.Title.Duration).DefaultIfEmpty(0).Max();
            times = InputBuilder.EvenPoints(end - start, (int)(_gradePoints.Value ?? 12)).Select(t => t + start).ToList();
        }
        var frames = _gradeFrames.IsChecked == true;
        await RunJobAsync("Remaster", async ct =>
        {
            var gc = new GradeCompare(_tools, _temp);
            var tc = new TemporalCompare(_tools, _temp);
            for (var i = 1; i < _items.Count; i++)
            {
                var prog = new Progress<string>(SetStatus);
                SetProgress((double)(i - 1) / (_items.Count - 1));
                _items[i].Grade = await gc.CompareAsync(_items[0], _items[i], times, prog, ct);
                RefreshGradeResults();
                if (frames && !_items[i].Title.IsStill && !_items[0].Title.IsStill)
                {
                    _items[i].Temporal = await tc.CompareAsync(_items[0], _items[i], 5, 4, prog, ct);
                    RefreshGradeResults();
                }
            }
        });
        RefreshGradeResults();
    }

    private void RefreshGradeResults()
    {
        _gradeResults.Children.Clear();
        var series = new List<ChartSeries>();
        var overTime = new List<ChartSeries>();
        for (var i = 1; i < _items.Count; i++)
        {
            if (_items[i].Grade is not { } g)
                continue;
            var findings = g.Findings.Concat(_items[i].Temporal?.Findings ?? []).ToList();
            _gradeResults.Children.Add(FindingsCard(i, $"{_items[i].Name} compared with {_items[0].Name} · {g.Frames.Count} frames", findings));
            var t = g.OverTime();
            if (t.Count > 1)
            {
                var times = t.Select(x => x.Time).ToArray();
                overTime.Add(new ChartSeries($"{i + 1} L*", Palette[i % Palette.Length], times, t.Select(x => x.MidL).ToArray()));
                overTime.Add(new ChartSeries($"{i + 1} b*", Palette[i % Palette.Length], times, t.Select(x => x.DeltaB).ToArray()) { Dashed = true });
            }
            var (r, o) = g.ToneCurve();
            if (r.Length > 1)
            {
                if (series.Count == 0)
                    series.Add(new ChartSeries("same", new SolidColorBrush(Color.Parse("#FF6A6D74")), [0, 100], [0, 100]));
                series.Add(new ChartSeries($"{i + 1}", Palette[i % Palette.Length], r, o));
            }
        }
        if (_gradeResults.Children.Count == 0)
            _gradeResults.Children.Add(Muted("Press “Compare with source 1”. Line the sources up first (Compare button) so the same frames are compared.", 13));
        _toneChart.YMin = 0;
        _toneChart.YMax = 100;
        _toneChart.SetSeries(series);
        _gradeTimeChart.IsVisible = overTime.Count > 0;
        _gradeTimeChart.SetSeries(overTime);
    }
}
