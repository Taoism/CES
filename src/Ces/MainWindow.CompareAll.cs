using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ces.Compare;
using Ces.Dialogs;
using Ces.Model;

namespace Ces;

/// <summary>"Compare all": sync, screenshots, video, audio, metrics, remaster and subtitles in one run, with an estimate.</summary>
public sealed partial class MainWindow
{
    /// <summary>A Compare all run is going on: jobs log their errors instead of showing a message box.</summary>
    private bool _batchRunning;
    private bool _batchStop;
    private readonly List<string> _batchErrors = [];
    private readonly List<string> _batchNotes = [];

    // remaining-time model of the running batch
    private List<PlannedStep> _batchSteps = [];
    private int _batchIndex;
    private readonly Stopwatch _batchStepClock = new();
    private double _batchFraction;
    private double _batchDoneEstimate;
    private double _batchDoneActual;

    private List<PlanSource> PlanSources()
    {
        var list = new List<PlanSource>();
        for (var i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var info = it.Title.Info;
            var v = info?.Video;
            var audio = info?.Audio.Count() ?? 0;
            if (audio > 0 && _audioTrackPick.TryGetValue(i, out var pick) && pick.SelectedIndex > 0)
                audio = 1;
            list.Add(new PlanSource
            {
                Duration = it.Title.IsStill ? 0 : it.Title.Duration,
                Width = v?.Width ?? 0,
                Height = v?.Height ?? 0,
                FrameRate = v?.FrameRate ?? 0,
                HasVideo = v != null && !it.Title.IsStill,
                IsStill = it.Title.IsStill,
                AudioTracks = audio,
                HasSubtitles = _subPick.TryGetValue(i, out var sp) && sp.SelectedIndex >= 0,
                SizeBytes = it.Title.SizeBytes > 0 ? it.Title.SizeBytes : info?.SizeBytes ?? 0,
                Synced = i == 0 || it.Synced,
                HasVideoResult = it.Video != null,
                HasAudioResult = it.Audio.Count > 0,
                HasGradeResult = it.Grade != null,
                HasMetricsResult = _metrics.Any(m => m.Distorted == it),
                HasSubtitleResult = _subReports.Any(r => r.Item == it),
            });
        }
        return list;
    }

    private PlanOptions PlanOptionsNow()
    {
        var n = _items.Count;
        var missing = _rows.Sum(r => Enumerable.Range(0, n).Count(i => i >= r.Row.Shots.Count || r.Row.Shots[i] == null));
        return new PlanOptions
        {
            SyncMode = _settings.SyncMode,
            ShotRows = _rows.Count,
            MissingShots = missing,
            NewShotCount = (int)(_shotCount.Value ?? 8),
            DetailedShots = _shotMode.SelectedIndex == 4,
            KeyframesScanned = n > 0 && _keyframeScans.ContainsKey(_items[0].Title),
            VideoPoints = (int)(_videoPoints.Value ?? 8),
            AudioFullLength = _audioFull.IsChecked == true,
            MetricLength = Format.TryParseTime(_qLength.Text, out var len) && len > 0 ? len : 60,
            MetricSubsample = (int)(_qSub.Value ?? 1),
            Vmaf = _qVmaf.IsChecked == true,
            Xpsnr = _qXpsnr.IsChecked == true,
            GradePoints = (int)(_gradePoints.Value ?? 12),
            GradeFrames = _gradeFrames.IsChecked == true,
        };
    }

    /// <summary>In a Compare all run a step that cannot run says why in the log; otherwise a message box.</summary>
    private async Task NotifyAsync(string text, MessageKind kind = MessageKind.Info, string title = "CES")
    {
        if (_batchRunning)
        {
            _batchNotes.Add(text);
            AppendLog((kind == MessageKind.Info ? "" : "WARNING ") + text);
            return;
        }
        await MessageBox.ShowAsync(this, text, kind, title);
    }

    /// <summary>Remaining seconds of the batch: the running step by its progress (or its estimate), the rest by
    /// their estimates scaled by how fast this run has been so far.</summary>
    private double BatchRemaining()
    {
        if (_batchIndex >= _batchSteps.Count)
            return 0;
        var ratio = _batchDoneEstimate > 10 ? Math.Clamp(_batchDoneActual / _batchDoneEstimate, 0.33, 3) : 1;
        var est = _batchSteps[_batchIndex].Seconds * ratio;
        var spent = _batchStepClock.Elapsed.TotalSeconds;
        var byModel = Math.Max(0, est - spent);
        var f = _batchFraction;
        // progress says more once a step is well under way; mix both before that
        var current = f > 0.05 && f < 1 ? (spent / f - spent) * Math.Min(1, f * 2) + byModel * (1 - Math.Min(1, f * 2)) : byModel;
        // an overrunning step: a few seconds at least, not "0 left"
        if (current < 3)
            current = 3;
        var future = _batchSteps.Skip(_batchIndex + 1).Sum(s => s.Seconds) * ratio;
        return current + future;
    }

    private async Task CompareAllAsync()
    {
        if (_tools == null || _items.Count == 0)
        {
            await MessageBox.ShowAsync(this, "Add the sources first (source 1 is the reference).", MessageKind.Info);
            return;
        }
        if (Busy)
        {
            await MessageBox.ShowAsync(this, "Another task is still running — wait for it or cancel it first.", MessageKind.Info);
            return;
        }
        var plan = CompareAllPlanner.Plan(PlanSources(), PlanOptionsNow(), _settings.CompareAllFactors);
        var chosen = await CompareAllDialog.ShowAsync(this, plan);
        if (chosen == null || chosen.Count == 0)
            return;

        _batchSteps = chosen.ToList();
        _batchIndex = 0;
        _batchDoneEstimate = _batchDoneActual = 0;
        _batchStop = false;
        _batchRunning = true;
        _batchErrors.Clear();
        var total = chosen.Sum(s => s.Seconds);
        AppendLog($"== Compare all: {string.Join(", ", chosen.Select(s => s.Name))} (estimated {CompareAllPlanner.Rough(total)})");
        _job.BeginBatch("Compare all", BatchRemaining);
        var results = new List<(PlannedStep Step, string Outcome, double Seconds)>();
        var all = Stopwatch.StartNew();
        try
        {
            for (_batchIndex = 0; _batchIndex < _batchSteps.Count && !_batchStop; _batchIndex++)
            {
                var step = _batchSteps[_batchIndex];
                _batchFraction = 0;
                _batchNotes.Clear();
                var errors = _batchErrors.Count;
                _batchStepClock.Restart();
                SetStatus($"Compare all · step {_batchIndex + 1} of {_batchSteps.Count}: {step.Name}…");
                await RunStepAsync(step);
                _batchStepClock.Stop();
                var secs = _batchStepClock.Elapsed.TotalSeconds;
                string outcome;
                if (_batchStop)
                    outcome = "stopped";
                else if (_batchErrors.Count > errors)
                    outcome = "failed: " + _batchErrors[^1];
                else if (_batchNotes.Count > 0)
                    outcome = "skipped: " + _batchNotes[0];
                else
                {
                    outcome = "done";
                    CompareAllPlanner.Learn(_settings.CompareAllFactors, step.Step, step.Seconds, secs);
                    _batchDoneEstimate += step.Seconds;
                    _batchDoneActual += secs;
                }
                results.Add((step, outcome, secs));
                AppendLog($"Compare all · {step.Name}: {outcome} in {Clock(secs)} (estimated {CompareAllPlanner.Rough(step.Seconds)})");
            }
        }
        finally
        {
            _batchRunning = false;
            _job.EndBatch();
            _settings.Save();
        }
        all.Stop();
        RefreshSpecs();

        var sb = new StringBuilder();
        foreach (var (step, outcome, secs) in results)
            sb.AppendLine(CultureInfo.InvariantCulture, $"{(outcome == "done" ? "✓" : outcome == "stopped" ? "■" : "–")}  {step.Name}: {outcome} · {Clock(secs)}");
        foreach (var step in _batchSteps.Skip(results.Count))
            sb.AppendLine(CultureInfo.InvariantCulture, $"■  {step.Name}: not run");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Total {Clock(all.Elapsed.TotalSeconds)} (estimated {CompareAllPlanner.Rough(total)}).");
        var stopped = _batchStop;
        SetStatus(stopped ? "Compare all — stopped." : $"Compare all — done in {Clock(all.Elapsed.TotalSeconds)}.");
        await MessageBox.ShowAsync(this, sb.ToString(), results.Any(r => r.Outcome.StartsWith("failed", StringComparison.Ordinal)) ? MessageKind.Warning : MessageKind.Info, stopped ? "Compare all — stopped" : "Compare all");
    }

    private async Task RunStepAsync(PlannedStep step)
    {
        switch (step.Step)
        {
            case CompareStep.Sync:
                await AutoSyncAsync(onlyUnsynced: !step.AlreadyDone, quiet: true);
                break;
            case CompareStep.Screenshots:
                if (_rows.Count == 0 || step.AlreadyDone)
                    await TakeShotsAsync(replace: true);
                else
                    await FillMissingAsync();
                break;
            case CompareStep.Video:
                await RunVideoAnalysisAsync();
                break;
            case CompareStep.Audio:
                await RunAudioAnalysisAsync();
                break;
            case CompareStep.Metrics:
                await RunMetricsAsync();
                break;
            case CompareStep.Remaster:
                await RunGradeAsync();
                break;
            case CompareStep.Subtitles:
                await CompareSubtitleTimingAsync();
                break;
        }
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
