using KeepingYouAwake.Core.Platform;
using Timer = System.Windows.Forms.Timer;

namespace KeepingYouAwake.Windows;

/// <summary>
/// <see cref="IScheduler"/> on WinForms timers: every callback runs on the UI thread.
/// One-shot timers re-check the wall clock at least once a minute, so they fire on time
/// even across system sleep or clock changes and support delays beyond a timer's range.
/// </summary>
internal sealed class WinFormsScheduler : IScheduler
{
    private static readonly TimeSpan MaxStep = TimeSpan.FromMinutes(1);

    public DateTimeOffset Now => DateTimeOffset.Now;

    public IDisposable After(TimeSpan delay, Action action)
    {
        var due = Now + delay;
        var timer = new Timer();
        var handle = new TimerHandle(timer);
        void Arm()
        {
            var remaining = due - Now;
            var step = remaining < MaxStep ? remaining : MaxStep;
            timer.Interval = Math.Max(1, (int)Math.Ceiling(step.TotalMilliseconds));
        }
        timer.Tick += (_, _) =>
        {
            if (Now < due)
            {
                Arm();
                return;
            }
            handle.Dispose();
            action();
        };
        Arm();
        timer.Start();
        return handle;
    }

    public IDisposable Every(TimeSpan interval, Action action)
    {
        var timer = new Timer { Interval = Math.Max(1, (int)Math.Min(int.MaxValue, interval.TotalMilliseconds)) };
        timer.Tick += (_, _) => action();
        timer.Start();
        return new TimerHandle(timer);
    }

    private sealed class TimerHandle(Timer timer) : IDisposable
    {
        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
        }
    }
}

/// <summary>Posts work from background threads (OS event threads, the pipe server) to the UI thread.</summary>
internal static class UiThread
{
    private static SynchronizationContext? context;

    /// <summary>Call once on the UI thread after the message loop's context is installed.</summary>
    public static void Capture() => context = SynchronizationContext.Current;

    public static void Post(Action action)
    {
        if (context is null) { action(); }
        else { context.Post(_ => action(), null); }
    }
}
