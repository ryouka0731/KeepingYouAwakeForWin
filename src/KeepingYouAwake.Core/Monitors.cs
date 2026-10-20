using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core;

/// <summary>
/// Polls a yes/no condition and raises <see cref="BecameTrue"/> / <see cref="BecameFalse"/>
/// on transitions. The first evaluation after <see cref="Start"/> counts as a transition
/// from false, so a condition that already holds fires <see cref="BecameTrue"/> right away.
/// </summary>
public class PollingConditionMonitor(IScheduler scheduler, TimeSpan interval, Func<bool>? condition = null) : IDisposable
{
    private IDisposable? tick;

    public event EventHandler? BecameTrue;
    public event EventHandler? BecameFalse;

    public bool IsRunning => tick is not null;
    public bool State { get; private set; }

    public void Start()
    {
        if (IsRunning) { return; }
        State = false;
        tick = scheduler.Every(interval, Evaluate);
        Evaluate();
    }

    /// <summary>Stops polling. Does not raise <see cref="BecameFalse"/>.</summary>
    public void Stop()
    {
        tick?.Dispose();
        tick = null;
        State = false;
    }

    /// <summary>Re-checks immediately (e.g. after a configuration or OS change notification).</summary>
    public void Evaluate()
    {
        if (!IsRunning) { return; }
        bool now;
        try { now = Check(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        if (now == State) { return; }
        State = now;
        (now ? BecameTrue : BecameFalse)?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    protected virtual bool Check() => condition?.Invoke() ?? false;
}

/// <summary>Detects in-progress browser / download-manager files in the watched folders.</summary>
public sealed class DownloadActivityMonitor : PollingConditionMonitor
{
    /// <summary>Partial-file suffixes of Chrome / Edge, Firefox, Safari-style, Opera, qBittorrent and µTorrent.</summary>
    public static readonly IReadOnlyList<string> InProgressSuffixes =
        [".crdownload", ".part", ".partial", ".download", ".opdownload", ".!qb", ".!ut"];

    private IReadOnlyList<string> directories = [];

    public DownloadActivityMonitor(IScheduler scheduler)
        : base(scheduler, TimeSpan.FromSeconds(10))
    {
    }

    protected override bool Check() => HasInProgressDownload();

    public void SetDirectories(IReadOnlyList<string> dirs)
    {
        directories = dirs.Select(d => Environment.ExpandEnvironmentVariables(d.Trim())).Where(d => d.Length > 0).ToList();
        Evaluate();
    }

    public bool HasInProgressDownload() => directories.Any(DirectoryHasInProgressFile);

    internal static bool DirectoryHasInProgressFile(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) { return false; }
            return Directory.EnumerateFiles(directory).Any(path =>
                InProgressSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>
/// Samples system CPU usage and reports when it stays above the threshold for
/// <see cref="Dwell"/> (and back below for the same time), so short spikes don't flap.
/// </summary>
public sealed class CpuLoadMonitor(IScheduler scheduler, ICpuTimesSampler sampler) : IDisposable
{
    private IDisposable? tick;
    private CpuTimes? last;
    private TimeSpan above;
    private TimeSpan below;

    public event EventHandler? CrossedAbove;
    public event EventHandler? FellBelow;

    public double ThresholdPercent { get; set; } = AppSettings.DefaultCpuLoadThreshold;
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Dwell { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsRunning => tick is not null;
    public bool IsAboveThreshold { get; private set; }
    public double? CurrentBusyPercent { get; private set; }

    public void Start()
    {
        if (IsRunning) { return; }
        Reset();
        tick = scheduler.Every(SamplingInterval, SampleOnce);
        SampleOnce();
    }

    public void Stop()
    {
        tick?.Dispose();
        tick = null;
        Reset();
    }

    public void Dispose() => Stop();

    private void Reset()
    {
        last = null;
        above = below = TimeSpan.Zero;
        IsAboveThreshold = false;
        CurrentBusyPercent = null;
    }

    internal void SampleOnce()
    {
        if (sampler.Sample() is not { } now) { return; }
        var previous = last;
        last = now;
        if (previous is not { } prev || BusyPercent(prev, now) is not { } busy) { return; }
        CurrentBusyPercent = busy;
        Accumulate(busy > ThresholdPercent);
        RaiseOnDwell();
    }

    /// <summary>CPU busy share between two samples; null when the counters didn't advance (or wrapped).</summary>
    internal static double? BusyPercent(CpuTimes previous, CpuTimes now)
    {
        if (now.Total <= previous.Total || now.Idle < previous.Idle) { return null; }
        var total = now.Total - previous.Total;
        var idle = Math.Min(now.Idle - previous.Idle, total);
        return 100.0 * (total - idle) / total;
    }

    private void Accumulate(bool isAbove)
    {
        if (isAbove)
        {
            above += SamplingInterval;
            below = TimeSpan.Zero;
        }
        else
        {
            below += SamplingInterval;
            above = TimeSpan.Zero;
        }
    }

    private void RaiseOnDwell()
    {
        if (!IsAboveThreshold && above >= Dwell)
        {
            IsAboveThreshold = true;
            CrossedAbove?.Invoke(this, EventArgs.Empty);
        }
        else if (IsAboveThreshold && below >= Dwell)
        {
            IsAboveThreshold = false;
            FellBelow?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Nudges the pointer every <see cref="Interval"/> while a session runs.</summary>
public sealed class MouseJiggler(IScheduler scheduler, IPointerNudger nudger) : IDisposable
{
    private IDisposable? tick;

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(60);
    public bool IsRunning => tick is not null;

    public void Start()
    {
        if (IsRunning) { return; }
        tick = scheduler.Every(Interval, nudger.Nudge);
        nudger.Nudge();
    }

    public void Stop()
    {
        tick?.Dispose();
        tick = null;
    }

    public void Dispose() => Stop();
}
