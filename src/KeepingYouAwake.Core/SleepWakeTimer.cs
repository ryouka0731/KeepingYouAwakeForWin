using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core;

/// <summary>
/// One sleep-prevention session: holds the OS power request and ends it at the
/// fire date (or never, for an indefinite session).
/// </summary>
public sealed class SleepWakeTimer(ISleepAssertion assertion, IScheduler scheduler)
{
    private IDisposable? expiry;
    private Action<bool>? completion;

    public bool IsScheduled { get; private set; }

    /// <summary>Requested length; null for indefinite. Only meaningful while scheduled.</summary>
    public TimeSpan? ScheduledInterval { get; private set; }

    /// <summary>When the session ends; null for indefinite or when not scheduled.</summary>
    public DateTimeOffset? FireDate { get; private set; }

    /// <summary>Raised right before a session starts.</summary>
    public event EventHandler? WillActivate;

    /// <summary>
    /// Starts a session. <paramref name="onEnded"/> runs once when it ends, with
    /// <c>true</c> when it was cancelled and <c>false</c> when it expired.
    /// </summary>
    public void Schedule(TimeSpan? interval, bool allowDisplaySleep, Action<bool>? onEnded)
    {
        if (IsScheduled) { End(cancelled: true); }

        ScheduledInterval = interval is { } i && i > TimeSpan.Zero ? i : null;
        FireDate = ScheduledInterval is { } span ? scheduler.Now + span : null;
        completion = onEnded;
        IsScheduled = true;
        WillActivate?.Invoke(this, EventArgs.Empty);

        assertion.Acquire(allowDisplaySleep);
        if (ScheduledInterval is { } length)
        {
            expiry = scheduler.After(length, () => End(cancelled: false));
        }
    }

    /// <summary>Re-applies the power request, e.g. after "Allow the display to sleep" changed.</summary>
    public void Reacquire(bool allowDisplaySleep)
    {
        if (IsScheduled) { assertion.Acquire(allowDisplaySleep); }
    }

    /// <summary>Cancels the running session. No-op when none is scheduled.</summary>
    public void Invalidate()
    {
        if (IsScheduled) { End(cancelled: true); }
    }

    private void End(bool cancelled)
    {
        expiry?.Dispose();
        expiry = null;
        assertion.Release();
        IsScheduled = false;
        ScheduledInterval = null;
        FireDate = null;

        // Clear first so a re-entrant Schedule from the callback gets its own completion.
        var done = completion;
        completion = null;
        done?.Invoke(cancelled);
    }
}
