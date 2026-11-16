using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NothingElseCompares2u.Analysis;
using NothingElseCompares2u.Compare;
using NothingElseCompares2u.Controls;
using NothingElseCompares2u.Dialogs;
using NothingElseCompares2u.Model;
using NothingElseCompares2u.Share;

namespace NothingElseCompares2u;

public sealed partial class MainWindow
{
    private string? _slowPicsUrl;

    // ---------- subtitles in screenshots ----------

    private readonly ComboBox _shotSubs = new() { ItemsSource = new[] { "Subtitles: off" }, SelectedIndex = 0, Width = 150 };

    /// <summary>Languages present in any source, for the screenshot subtitle picker.</summary>
    private void RefreshSubtitleChoices()
    {
        var current = _shotSubs.SelectedItem as string;
        var langs = _items.SelectMany(i => i.Title.Info?.Subtitles ?? []).Select(s => s.Language).Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var list = new List<string> { "Subtitles: off" };
        list.AddRange(langs.Select(l => "Subtitles: " + l));
        if (_items.Any(i => i.Title.Info?.Subtitles.Any() == true))
            list.Add("Subtitles: track 1");
        _shotSubs.ItemsSource = list;
        _shotSubs.SelectedItem = current != null && list.Contains(current) ? current : list[0];
    }

    /// <summary>"eng", "1" (track number) or null.</summary>
    private string? SubtitleSpec() => _shotSubs.SelectedItem is string s && s != "Subtitles: off"
        ? s.EndsWith("track 1", StringComparison.Ordinal) ? "1" : s["Subtitles: ".Length..]
        : null;

    private ShotOptions ShotOptsFor(ShotOptions opt, CompareItem item) => opt.With(SubtitleChoice.Pick(item.Title, SubtitleSpec()));

    // ---------- subtitle timing tab ----------

    private readonly StackPanel _subSetup = new() { Spacing = 6 };
    private readonly Dictionary<int, ComboBox> _subPick = [];
    private readonly StackPanel _subResults = new() { Spacing = 8 };
    private readonly LineChart _subChart = new() { Title = "Subtitle shift against the reference over time (ms)", Height = 230 };
    private readonly List<SubtitleTimingReport> _subReports = [];

    private DockPanel BuildSubsTab()
    {
        var run = new Button { Content = "Compare timing", Classes = { "accent" } };
        run.Click += async (_, _) => await CompareSubtitleTimingAsync();
        _busyDisabled.Add(run);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 6 };
        bar.Children.AddRange([run, Muted("Each source's subtitle lines are matched to source 1's on the shared timeline: shift, spread, drift, missing lines. Reads the whole track (container only, fast).")]);
        foreach (var c in bar.Children.OfType<Control>())
            c.VerticalAlignment = VerticalAlignment.Center;
        _subChart.XFormat = v => Format.Time(v, false);
        _subChart.YFormat = v => v.ToString("0", CultureInfo.InvariantCulture) + " ms";
        var content = new StackPanel { Spacing = 12, Children = { new Border { Classes = { "card" }, Child = _subSetup }, _subResults, _subChart } };
        var root = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new ScrollViewer { Content = content, Margin = new Thickness(0, 8, 0, 0) });
        RefreshSubsSetup();
        return root;
    }

    private void RefreshSubsSetup()
    {
        _subSetup.Children.Clear();
        _subPick.Clear();
        _subSetup.Children.Add(new TextBlock { Text = "Subtitle tracks", Classes = { "header" } });
        if (_items.Count == 0)
        {
            _subSetup.Children.Add(Muted("No sources yet."));
            return;
        }
        string? refLang = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var subs = _items[i].Title.Info?.Subtitles.ToList() ?? [];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 10 };
            row.Children.Add(ChipLabel(i, _items[i].Name));
            Control pick;
            if (subs.Count == 0)
            {
                pick = Muted("no subtitles");
            }
            else
            {
                // default: the reference's first track; the others: the same language
                var sel = i == 0 ? 0 : SubtitleChoice.Pick(_items[i].Title, refLang) ?? 0;
                var combo = new ComboBox { ItemsSource = subs.Select((s, k) => $"{k + 1}: {s.Label}").ToList(), SelectedIndex = sel, MinWidth = 420 };
                if (i == 0)
                    refLang = subs[0].Language;
                _subPick[i] = combo;
                pick = combo;
            }
            Grid.SetColumn(pick, 1);
            row.Children.Add(pick);
            _subSetup.Children.Add(row);
        }
    }

    private async Task CompareSubtitleTimingAsync()
    {
        if (_tools == null || _items.Count < 2 || !_subPick.TryGetValue(0, out var refPick))
        {
            await MessageBox.ShowAsync(this, "Add at least two sources; source 1 needs a subtitle track.", MessageKind.Info);
            return;
        }
        _subReports.Clear();
        await RunJobAsync("Subtitle timing", async ct =>
        {
            var st = new SubtitleTiming(_tools);
            var refTrack = refPick.SelectedIndex;
            SetStatus("Reading the reference subtitles…");
            var refEvents = await st.ReadAsync(_items[0].Title, refTrack, null, ct);
            for (var i = 1; i < _items.Count; i++)
            {
                if (!_subPick.TryGetValue(i, out var c) || c.SelectedIndex < 0)
                    continue;
                SetStatus($"Reading subtitles of {_items[i].Name}…");
                SetProgress((double)i / _items.Count);
                var ev = await st.ReadAsync(_items[i].Title, c.SelectedIndex, null, ct);
                _subReports.Add(SubtitleTiming.Compare(_items[0], refEvents, _items[i], c.SelectedIndex, ev));
            }
            AppendLog($"Subtitle timing: reference has {refEvents.Count} lines");
        });
        RefreshSubResults();
    }

    private void RefreshSubResults()
    {
        _subResults.Children.Clear();
        var series = new List<ChartSeries>();
        foreach (var r in _subReports.Where(r => _items.Contains(r.Item)))
        {
            var i = _items.IndexOf(r.Item);
            _subResults.Children.Add(FindingsCard(i, $"{r.Item.Name} · {r.TrackLabel}", r.Findings));
            if (r.Deltas.Count > 1)
                series.Add(new ChartSeries($"{i + 1}", Palette[i % Palette.Length], r.Deltas.Select(d => d.Time).ToArray(), r.Deltas.Select(d => d.Delta * 1000).ToArray()));
        }
        if (_subResults.Children.Count == 0)
            _subResults.Children.Add(Muted("Pick the tracks above and press “Compare timing”.", 13));
        _subChart.SetSeries(series);
    }

    // ---------- share ----------

    private async Task UploadSlowPicsAsync()
    {
        var rows = SlowPics.FromRows(_items, _rows.Select(r => r.Row));
        if (rows.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Take screenshots first (rows where every source has a picture are uploaded).", MessageKind.Info);
            return;
        }
        var publicBox = new CheckBox { Content = "List publicly on slow.pics (otherwise only people with the link see it)" };
        var name = new TextBox { Text = string.Join(" vs ", _items.Select(i => i.Name)) };
        var body = DialogWindow.Column(10,
            DialogWindow.Label($"Upload {rows.Count} rows × {_items.Count} screenshots ({rows.Sum(r => r.Images.Count)} PNGs) to slow.pics, a public image host? Anyone with the link can see them; you cannot delete an anonymous upload yourself."),
            DialogWindow.Label("Comparison name"), name, publicBox);
        var dlg = new ConfirmDialog("Upload to slow.pics", body, "Upload");
        if (!await dlg.ShowDialog<bool>(this))
            return;
        string? url = null;
        await RunJobAsync("Uploading to slow.pics", async ct =>
        {
            using var sp = new SlowPics();
            var prog = new Progress<(double Fraction, string Text)>(p =>
            {
                SetProgress(p.Fraction);
                SetStatus(p.Text);
            });
            url = await sp.UploadAsync(name.Text?.Trim() is { Length: > 0 } n ? n : "Comparison", rows, publicBox.IsChecked == true, prog, ct);
        });
        if (url == null)
            return;
        _slowPicsUrl = url;
        AppendLog("slow.pics: " + url);
        if (Clipboard != null)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, url);
        SetStatus("Uploaded: " + url + " (copied)");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            await MessageBox.ShowAsync(this, url, MessageKind.Info, "slow.pics");
        }
    }

    private async Task CopySummaryAsync(SummaryFormat fmt)
    {
        if (_items.Count == 0)
            return;
        var text = Summary.Build(fmt, _items, _ranking.Count > 0 ? _ranking : null, _slowPicsUrl);
        if (Clipboard != null)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(Clipboard, text);
        SetStatus($"{fmt} summary copied to the clipboard{(_slowPicsUrl == null ? " (upload screenshots to slow.pics to include a link)" : "")}.");
    }

    // ---------- comparison findings for the analysis tabs ----------

    private Border CrossCard(int index, string title, IEnumerable<Finding> own, IEnumerable<Finding> cross)
    {
        var all = own.ToList();
        all.AddRange(cross.Select(f => new Finding { Area = "vs 1 · " + f.Area, Text = f.Text, Level = f.Level }));
        return FindingsCard(index, title, all);
    }
}

/// <summary>A yes/no dialog with custom content.</summary>
public sealed class ConfirmDialog : DialogWindow
{
    public ConfirmDialog(string title, Control body, string ok) : base(title, 520) => SetBody(body, ok);
}
