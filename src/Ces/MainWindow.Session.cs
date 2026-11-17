using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ces.Analysis;
using Ces.Compare;
using Ces.Dialogs;
using Ces.Disc;
using Ces.Probe;

namespace Ces;

public sealed partial class MainWindow
{
    /// <summary>Saved comparison ("CES comparison").</summary>
    public const string SessionExtension = ".cesc";
    private string? _sessionPath;
    private MenuItem? _recentMenu;

    private static readonly FilePickerFileType SessionType = new("CES comparison") { Patterns = ["*" + SessionExtension] };

    private async Task SaveSessionAsync(bool saveAs)
    {
        if (_items.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Nothing to save yet — add sources first.", MessageKind.Info);
            return;
        }
        var path = _sessionPath;
        if (saveAs || path == null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save comparison",
                DefaultExtension = SessionExtension,
                SuggestedFileName = SafeName(string.Join(" vs ", _items.Take(3).Select(i => i.Name))) + SessionExtension,
                FileTypeChoices = [SessionType],
                SuggestedStartLocation = await StartFolderAsync(),
            });
            path = file?.TryGetLocalPath();
            if (path == null)
                return;
        }
        var s = new SessionFile();
        foreach (var it in _items)
        {
            var src = new SessionSource
            {
                Path = it.Source.Path,
                Title = it.Title.Name,
                Label = it.Label,
                Offset = it.Sections.Count > 0 ? it.Sections.First(x => x.Offset != null).Offset!.Value : it.Offset,
                Speed = it.Speed,
                Synced = it.Synced,
                Sections = it.Sections.Select(x => new SyncSection { Start = x.Start, Offset = x.Offset }).ToList(),
                Video = it.Video,
                Audio = it.Audio,
                GradeFrames = it.Grade?.Frames ?? [],
                GradeFindings = it.Grade?.Findings ?? [],
                TemporalFindings = it.Temporal?.Findings ?? [],
                FrameEvents = it.Temporal?.Events ?? [],
                Ivtc = it.Ivtc,
            };
            foreach (var a in it.Audio)
            {
                if (a.SpectrogramPng != null && File.Exists(a.SpectrogramPng))
                    src.Spectrograms[a.TrackIndex] = Convert.ToBase64String(await File.ReadAllBytesAsync(a.SpectrogramPng, CancellationToken.None));
            }
            s.Sources.Add(src);
        }
        s.Rows = _rows.Select(r => new SessionRow { Time = r.Row.Time, Caption = r.Row.Caption }).ToList();
        s.Metrics = _metrics.Select(m => new SessionMetric { Reference = _items.IndexOf(m.Reference), Distorted = _items.IndexOf(m.Distorted), Result = m.Result }).ToList();
        s.Ranking = _ranking.Select(r => new SessionRank { Item = _items.IndexOf(r.Item), Rank = r.Rank, Error = r.Error }).ToList();
        try
        {
            await s.SaveAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageBox.ShowAsync(this, "Could not save:\n" + ex.Message, MessageKind.Error);
            return;
        }
        _sessionPath = path;
        RememberRecent(path);
        UpdateTitle();
        SetStatus("Saved " + path);
    }

    private async Task OpenSessionAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open comparison",
            FileTypeFilter = [SessionType],
            SuggestedStartLocation = await StartFolderAsync(),
        });
        if ((files.Count > 0 ? files[0].TryGetLocalPath() : null) is { } p)
            await LoadSessionAsync(p);
    }

    private async Task LoadSessionAsync(string path)
    {
        if (_tools == null)
            return;
        SessionFile s;
        try
        {
            s = await SessionFile.LoadAsync(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            await MessageBox.ShowAsync(this, $"Could not read {Path.GetFileName(path)}:\n{ex.Message}", MessageKind.Error);
            return;
        }
        // start from a clean slate
        ClearShots();
        foreach (var it in _items.ToList())
            await SourceOpener.CloseAsync(it.Source);
        _items.Clear();
        _metrics.Clear();
        _ranking = [];
        _keyframeScans.Clear();

        var problems = new List<string>();
        var map = new Dictionary<int, CompareItem>();
        await RunJobAsync("Opening comparison", async ct =>
        {
            for (var i = 0; i < s.Sources.Count; i++)
            {
                var ss = s.Sources[i];
                SetStatus("Opening " + ss.Path);
                try
                {
                    var src = await SourceOpener.OpenAsync(ss.Path, _settings.IncludeShortPlaylists, ct);
                    var title = src.Titles.FirstOrDefault(t => t.Name == ss.Title) ?? src.MainTitle!;
                    if (title.Name != ss.Title)
                        problems.Add($"{ss.Path}: title {ss.Title} not found, using {title.Name}");
                    await new MediaProber(_tools).ProbeAsync(src, title, ct);
                    var item = new CompareItem { Source = src, Title = title, Label = ss.Label, Speed = ss.Speed };
                    item.Offset = ss.Offset;
                    item.Sections.AddRange(ss.Sections);
                    item.Synced = ss.Synced;
                    item.Video = ss.Video;
                    item.Ivtc = ss.Ivtc;
                    if (ss.GradeFrames.Count > 0 || ss.GradeFindings.Count > 0)
                    {
                        item.Grade = new GradeReport { Item = item };
                        item.Grade.Frames.AddRange(ss.GradeFrames);
                        item.Grade.Findings.AddRange(ss.GradeFindings);
                    }
                    if (ss.TemporalFindings.Count > 0)
                    {
                        item.Temporal = new TemporalReport { Item = item };
                        item.Temporal.Findings.AddRange(ss.TemporalFindings);
                        item.Temporal.Events.AddRange(ss.FrameEvents);
                    }
                    foreach (var a in ss.Audio)
                    {
                        if (ss.Spectrograms.TryGetValue(a.TrackIndex, out var b64))
                        {
                            var png = Path.Combine(_temp, $"spectrogram-{Guid.NewGuid():N}.png");
                            Directory.CreateDirectory(_temp);
                            await File.WriteAllBytesAsync(png, Convert.FromBase64String(b64), ct);
                            a.SpectrogramPng = png;
                        }
                        else
                        {
                            a.SpectrogramPng = null;
                        }
                        item.Audio.Add(a);
                    }
                    _items.Add(item);
                    map[i] = item;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    problems.Add($"{ss.Path}: {ex.Message}");
                }
            }
            foreach (var m in s.Metrics)
            {
                if (m.Result != null && map.TryGetValue(m.Reference, out var rf) && map.TryGetValue(m.Distorted, out var ds))
                    _metrics.Add((rf, ds, m.Result));
            }
            foreach (var r in s.Ranking)
            {
                if (map.TryGetValue(r.Item, out var it))
                    _ranking.Add(new RankRow { Item = it, Rank = r.Rank, Error = r.Error, Result = _metrics.FirstOrDefault(x => x.Distorted == it).Result });
            }
            SourcesChanged();
        });
        _sessionPath = path;
        RememberRecent(path);
        UpdateTitle();
        if (s.Rows.Count > 0 && _items.Count > 0)
            await AddShotTimesAsync(s.Rows.Select(r => (r.Time, r.Caption)));
        if (problems.Count > 0)
            await MessageBox.ShowAsync(this, "Opened with problems:\n\n" + string.Join("\n", problems), MessageKind.Warning);
    }

    private void UpdateTitle() =>
        Title = _sessionPath != null ? $"{Path.GetFileNameWithoutExtension(_sessionPath)} — CES" : "CES — Compare Enc Spot";

    private void RememberRecent(string path)
    {
        _settings.RecentSessions.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentSessions.Insert(0, path);
        if (_settings.RecentSessions.Count > 8)
            _settings.RecentSessions.RemoveRange(8, _settings.RecentSessions.Count - 8);
        _settings.Save();
        RebuildRecentMenu();
    }

    private void RebuildRecentMenu()
    {
        if (_recentMenu == null)
            return;
        _recentMenu.Items.Clear();
        foreach (var p in _settings.RecentSessions)
        {
            var mi = new MenuItem { Header = p };
            var path = p;
            mi.Click += async (_, _) =>
            {
                if (File.Exists(path))
                    await LoadSessionAsync(path);
                else
                    await MessageBox.ShowAsync(this, "Not found:\n" + path, MessageKind.Warning);
            };
            _recentMenu.Items.Add(mi);
        }
        _recentMenu.IsEnabled = _settings.RecentSessions.Count > 0;
    }
}
