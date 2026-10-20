using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core;

/// <summary>
/// An Active Hours window: a set of weekdays and a time range in minutes after
/// midnight. When <see cref="EndMinutes"/> ≤ <see cref="StartMinutes"/> the window
/// wraps past midnight into the next day.
/// </summary>
public sealed class ScheduleWindow
{
    public const int MinutesPerDay = 24 * 60;

    public List<DayOfWeek> Weekdays { get; set; } = [];
    public int StartMinutes { get; set; }
    public int EndMinutes { get; set; }
    public bool Enabled { get; set; } = true;

    public ScheduleWindow Normalized() => new()
    {
        Weekdays = (Weekdays ?? []).Where(Enum.IsDefined).Distinct().Order().ToList(),
        StartMinutes = Math.Clamp(StartMinutes, 0, MinutesPerDay - 1),
        EndMinutes = Math.Clamp(EndMinutes, 0, MinutesPerDay - 1),
        Enabled = Enabled,
    };

    public bool Contains(DateTime localTime)
    {
        if (!Enabled || Weekdays is not { Count: > 0 }) { return false; }
        var start = Math.Clamp(StartMinutes, 0, MinutesPerDay - 1);
        var end = Math.Clamp(EndMinutes, 0, MinutesPerDay - 1);
        var minutes = localTime.Hour * 60 + localTime.Minute;
        var today = localTime.DayOfWeek;

        if (end > start)
        {
            // Same-day window: [start, end).
            return Weekdays.Contains(today) && minutes >= start && minutes < end;
        }

        // Wraps past midnight: today's part is [start, 24:00), yesterday's part is [00:00, end).
        if (Weekdays.Contains(today) && minutes >= start) { return true; }
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);
        return Weekdays.Contains(yesterday) && minutes < end;
    }
}

/// <summary>Raises <see cref="Entered"/> / <see cref="Left"/> as the clock crosses Active Hours boundaries.</summary>
public sealed class ScheduleMonitor(IScheduler scheduler, Func<DateTimeOffset, DateTime>? toLocalTime = null) : IDisposable
{
    private readonly Func<DateTimeOffset, DateTime> toLocal = toLocalTime ?? (t => t.LocalDateTime);
    private IReadOnlyList<ScheduleWindow> windows = [];
    private IDisposable? tick;
    private bool inside;

    public event EventHandler? Entered;
    public event EventHandler? Left;

    public bool IsRunning => tick is not null;

    public void SetWindows(IReadOnlyList<ScheduleWindow> newWindows)
    {
        windows = newWindows;
        if (IsRunning) { Evaluate(); }
    }

    public void Start()
    {
        if (IsRunning) { return; }
        inside = false;
        tick = scheduler.Every(TimeSpan.FromSeconds(30), Evaluate);
        Evaluate();
    }

    public void Stop()
    {
        tick?.Dispose();
        tick = null;
        inside = false;
    }

    public void Dispose() => Stop();

    private void Evaluate()
    {
        var now = toLocal(scheduler.Now);
        var nowInside = windows.Any(w => w.Contains(now));
        if (nowInside == inside) { return; }
        inside = nowInside;
        (nowInside ? Entered : Left)?.Invoke(this, EventArgs.Empty);
    }
}
