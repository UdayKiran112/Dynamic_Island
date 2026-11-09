using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public sealed class CountdownTimer : ObservableObject
{
    private TimeSpan _remaining;

    public CountdownTimer(string label, TimeSpan duration)
    {
        Label = label;
        Duration = duration;
        _remaining = duration;
    }

    public string Label { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Remaining => _remaining;

    public string RemainingText => Format(_remaining);

    public double Progress => Duration.TotalSeconds <= 0
        ? 0
        : Math.Clamp(_remaining.TotalSeconds / Duration.TotalSeconds * 100, 0, 100);

    internal void Update(TimeSpan remaining)
    {
        _remaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
    }

    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}

/// <summary>
/// Countdown timers plus a stopwatch. One lightweight 200 ms ticker runs only
/// while something is active and stops itself when everything is idle.
/// </summary>
public sealed class TimerService : IDisposable
{
    private readonly DispatcherTimer _ticker;
    private readonly Stopwatch _stopwatch = new();
    private DateTime _lastTickUtc;
    private bool _stopwatchRunning;

    public ObservableCollection<CountdownTimer> Timers { get; } = new();

    public event EventHandler<CountdownTimer>? TimerFinished;
    public event EventHandler? StopwatchChanged;

    public bool IsStopwatchRunning => _stopwatchRunning;
    public TimeSpan StopwatchElapsed => _stopwatch.Elapsed;

    public TimerService(Dispatcher dispatcher)
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _ticker.Tick += (_, _) => Tick();
    }

    public CountdownTimer AddTimer(TimeSpan duration, string? label = null)
    {
        if (duration <= TimeSpan.Zero)
            duration = TimeSpan.FromMinutes(1);

        var timer = new CountdownTimer(label ?? $"Timer {FormatLabel(duration)}", duration);
        Timers.Insert(0, timer);
        EnsureTicking();
        return timer;
    }

    public void CancelTimer(CountdownTimer timer)
    {
        Timers.Remove(timer);
        StopIfIdle();
    }

    public void StartStopwatch()
    {
        if (_stopwatchRunning)
            return;

        _stopwatch.Start();
        _stopwatchRunning = true;
        EnsureTicking();
        StopwatchChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PauseStopwatch()
    {
        if (!_stopwatchRunning)
            return;

        _stopwatch.Stop();
        _stopwatchRunning = false;
        StopwatchChanged?.Invoke(this, EventArgs.Empty);
        StopIfIdle();
    }

    public void ResetStopwatch()
    {
        _stopwatch.Reset();
        _stopwatchRunning = false;
        StopwatchChanged?.Invoke(this, EventArgs.Empty);
        StopIfIdle();
    }

    private void EnsureTicking()
    {
        if (_ticker.IsEnabled)
            return;

        _lastTickUtc = DateTime.UtcNow;
        _ticker.Start();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        var delta = now - _lastTickUtc;
        _lastTickUtc = now;

        foreach (var timer in Timers.ToList())
        {
            var remaining = timer.Remaining - delta;
            if (remaining <= TimeSpan.Zero)
            {
                Timers.Remove(timer);
                TimerFinished?.Invoke(this, timer);
            }
            else
            {
                timer.Update(remaining);
            }
        }

        if (_stopwatchRunning)
            StopwatchChanged?.Invoke(this, EventArgs.Empty);

        StopIfIdle();
    }

    private void StopIfIdle()
    {
        if (Timers.Count == 0 && !_stopwatchRunning)
            _ticker.Stop();
    }

    private static string FormatLabel(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return $"{duration.TotalSeconds:0}s";

        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}h {duration.Minutes}m"
            : $"{duration.TotalMinutes:0.#}m";
    }

    public void Dispose()
    {
        _ticker.Stop();
        _stopwatch.Stop();
    }
}
