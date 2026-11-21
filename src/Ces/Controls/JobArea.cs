using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Ces.Controls;

/// <summary>
/// Bottom-right job state of the status bar, always visible: "Idle" or the running job with count, elapsed time and
/// progress, and a Stop button that is enabled while something runs.
/// </summary>
public sealed class JobArea : StackPanel
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, MinWidth = 150, TextAlignment = Avalonia.Media.TextAlignment.Right };
    private readonly ProgressBar _bar = new() { Width = 200, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Button _stop = new() { Content = "Stop", Classes = { "small" }, IsEnabled = false, MinWidth = 64, HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly Hourglass _glass = new() { VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _tick;
    private string? _name;
    private int _done;
    private int _total;
    private string? _batch;
    private Func<double>? _remaining;

    public JobArea()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 10;
        Children.Add(_glass);
        Children.Add(_text);
        Children.Add(_bar);
        Children.Add(_stop);
        ToolTip.SetTip(_stop, "Stop the running task");
        _stop.Click += (_, _) => StopRequested?.Invoke();
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _glass.Tick();
            Render();
        });
        Render();
    }

    /// <summary>The user pressed Stop.</summary>
    public event Action? StopRequested;

    public bool IsRunning => _name != null;

    /// <summary>The running job's name, or null when idle.</summary>
    public string? JobName => _name;

    /// <summary>A job started: progress is indeterminate until <see cref="Report(double)"/> or <see cref="Count"/>.</summary>
    public void Start(string name)
    {
        if (_name == null)
            _clock.Restart();
        _name = _batch != null ? $"{_batch} · {name}" : name;
        _done = _total = 0;
        _bar.IsVisible = true;
        _bar.IsIndeterminate = true;
        _stop.IsEnabled = true;
        _tick.Start();
        Render();
    }

    /// <summary>Progress as a fraction 0…1.</summary>
    public void Report(double fraction)
    {
        if (_name == null)
            return;
        _bar.IsIndeterminate = false;
        _bar.Value = Math.Clamp(fraction, 0, 1);
    }

    /// <summary>Progress as items done of a total (shown as "n / m").</summary>
    public void Count(int done, int total)
    {
        if (_name == null)
            return;
        _done = done;
        _total = total;
        Report(total > 0 ? (double)done / total : 0);
        Render();
    }

    /// <summary>Back to idle.</summary>
    public void Finish()
    {
        if (_batch != null)
        {
            // between the steps of a batch: keep the clock, the hourglass and Stop
            _done = _total = 0;
            _bar.IsIndeterminate = true;
            Render();
            return;
        }
        _name = null;
        _clock.Stop();
        _tick.Stop();
        _bar.IsVisible = false;
        _stop.IsEnabled = false;
        Render();
    }

    /// <summary>
    /// Several jobs run as one (Compare all): one clock for all of them, an hourglass that drains as
    /// <paramref name="remainingSeconds"/> counts down, and "about n min left" next to the elapsed time.
    /// </summary>
    public void BeginBatch(string name, Func<double> remainingSeconds)
    {
        _batch = name;
        _remaining = remainingSeconds;
        Start(name);
        _glass.Fraction = 0;
        _glass.IsVisible = true;
    }

    public void EndBatch()
    {
        _batch = null;
        _remaining = null;
        _glass.IsVisible = false;
        Finish();
    }

    /// <summary>Elapsed time of the running job or batch.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    private void Render()
    {
        if (_name == null)
        {
            _text.Text = "Idle";
            return;
        }
        var e = _clock.Elapsed;
        var time = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : e.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var text = _total > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{_name} · {_done} / {_total} · {time}")
            : $"{_name} · {time}";
        if (_remaining != null)
        {
            var left = Math.Max(0, _remaining());
            var spent = e.TotalSeconds;
            _glass.Fraction = spent + left > 0 ? spent / (spent + left) : 0;
            text += " · " + Ces.Compare.CompareAllPlanner.Left(left);
            ToolTip.SetTip(_glass, $"Elapsed {time}, {Ces.Compare.CompareAllPlanner.Left(left)} (rough estimate)");
        }
        _text.Text = text;
    }
}
