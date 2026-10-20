using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core;

/// <summary>The OS services the controller depends on.</summary>
public sealed record PlatformServices(
    IScheduler Scheduler,
    ISleepAssertion SleepAssertion,
    IPowerSource PowerSource,
    ISessionEvents SessionEvents,
    IDisplayMonitor DisplayMonitor,
    IWiFiProbe WiFiProbe,
    IProcessLister ProcessLister,
    IAudioOutputProbe AudioOutputProbe,
    ICpuTimesSampler CpuSampler,
    IPointerNudger PointerNudger,
    IDriveEnumerator DriveEnumerator);

public sealed record SessionNotification(bool Activating, DateTimeOffset? FireDate);

/// <summary>
/// Owns the sleep-wake session and every trigger that starts or ends one. All members
/// must be called on the UI thread (the scheduler and platform events deliver there).
/// </summary>
/// <remarks>
/// Invariant: a feature trigger only starts a session when none is running, and only
/// ends the session it started (<see cref="TerminateIfOwnedBy"/>). User sessions are
/// never ended by triggers, only by the user, battery rules or expiry.
/// </remarks>
public sealed class AppController : IDisposable
{
    private static readonly TimeSpan WiFiPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AudioPollInterval = TimeSpan.FromSeconds(5);

    private readonly PlatformServices platform;
    private readonly SettingsStore store;
    private readonly ActivityLogger log;
    private readonly SleepWakeTimer timer;
    private readonly ActivationOwnership ownership = new();

    private readonly PollingConditionMonitor wifiMonitor;
    private readonly PollingConditionMonitor appMonitor;
    private readonly PollingConditionMonitor audioMonitor;
    private readonly DownloadActivityMonitor downloadMonitor;
    private readonly ScheduleMonitor scheduleMonitor;
    private readonly CpuLoadMonitor cpuMonitor;

    private MouseJiggler? jiggler;
    private DriveAliveTimer? driveAlive;

    /// <summary>Bumped on every activation so a replaced session's late completion is ignored.</summary>
    private int sessionGeneration;

    /// <summary>Started below the battery threshold by the user: the threshold rule is ignored for this session.</summary>
    private bool batteryOverride;

    private bool acTriggerWasOnAC;
    private bool acTriggerEngaged;
    private bool displayTriggerEngaged;

    /// <summary>Session suspended by fast user switching, to resume on return. Null when there is none.</summary>
    private (TimeSpan? Interval, ActivationSource Source)? suspendedSession;

    private bool lastAllowDisplaySleep;
    private IReadOnlyList<string>? reconciledSsids;
    private IReadOnlyList<string>? reconciledApps;
    private bool disposed;

    public AppController(PlatformServices platform, SettingsStore store, ActivityLogger log)
    {
        this.platform = platform;
        this.store = store;
        this.log = log;
        Durations = new ActivationDurationsController(store);
        timer = new SleepWakeTimer(platform.SleepAssertion, platform.Scheduler);
        timer.WillActivate += (_, _) => OnSessionWillActivate();

        var s = platform.Scheduler;
        wifiMonitor = new PollingConditionMonitor(s, WiFiPollInterval,
            () => WatchedItems.AnyJoined(Settings.EnabledWatchedWiFiSsids(), platform.WiFiProbe.CurrentSsids()));
        appMonitor = new PollingConditionMonitor(s, ProcessPollInterval,
            () => WatchedItems.AnyRunning(Settings.EnabledWatchedApplications(), platform.ProcessLister.RunningExecutableNames()));
        audioMonitor = new PollingConditionMonitor(s, AudioPollInterval,
            () => platform.AudioOutputProbe.IsDefaultOutputExternal() == true);
        downloadMonitor = new DownloadActivityMonitor(s);
        scheduleMonitor = new ScheduleMonitor(s);
        cpuMonitor = new CpuLoadMonitor(s, platform.CpuSampler);

        wifiMonitor.BecameTrue += (_, _) => ActivateForTrigger(ActivationSource.WatchedSsid);
        wifiMonitor.BecameFalse += (_, _) => TerminateIfOwnedBy(ActivationSource.WatchedSsid);
        appMonitor.BecameTrue += (_, _) => ActivateForTrigger(ActivationSource.WatchedApp);
        appMonitor.BecameFalse += (_, _) => TerminateIfOwnedBy(ActivationSource.WatchedApp);
        audioMonitor.BecameTrue += (_, _) => ActivateForTrigger(ActivationSource.AudioOutput);
        audioMonitor.BecameFalse += (_, _) => TerminateIfOwnedBy(ActivationSource.AudioOutput);
        downloadMonitor.BecameTrue += (_, _) => ActivateForTrigger(ActivationSource.Download);
        downloadMonitor.BecameFalse += (_, _) => TerminateIfOwnedBy(ActivationSource.Download);
        scheduleMonitor.Entered += (_, _) => ActivateForTrigger(ActivationSource.Schedule);
        scheduleMonitor.Left += (_, _) => TerminateIfOwnedBy(ActivationSource.Schedule);
        cpuMonitor.CrossedAbove += (_, _) => ActivateForTrigger(ActivationSource.CpuLoad);
        cpuMonitor.FellBelow += (_, _) => TerminateIfOwnedBy(ActivationSource.CpuLoad);

        platform.PowerSource.Changed += OnPowerChanged;
        platform.DisplayMonitor.Changed += OnDisplaysChanged;
        platform.SessionEvents.SessionBecameActive += OnSessionBecameActive;
        platform.SessionEvents.SessionResignedActive += OnSessionResignedActive;
        store.Changed += OnSettingsChanged;
        lastAllowDisplaySleep = Settings.AllowDisplaySleep;
    }

    public ActivationDurationsController Durations { get; }

    public bool IsActive => timer.IsScheduled;
    public DateTimeOffset? FireDate => timer.FireDate;

    /// <summary>The running session's duration; null when inactive.</summary>
    public ActivationDuration? CurrentDuration =>
        timer.IsScheduled ? new ActivationDuration((long)Math.Round(timer.ScheduledInterval?.TotalSeconds ?? 0)) : null;

    /// <summary>Who started the running session; null when inactive.</summary>
    public ActivationSource? CurrentSource => ownership.IsActive ? ownership.Source : null;

    /// <summary>Raised whenever a session starts or ends.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised to show a "preventing sleep" / "allowing sleep" notification.</summary>
    public event EventHandler<SessionNotification>? NotificationRequested;

    /// <summary>Raised when the app should exit (Quit when activation duration is over).</summary>
    public event EventHandler? QuitRequested;

    private AppSettings Settings => store.Settings;

    /// <summary>Call once after the UI is ready: repairs the log, starts triggers, activates on launch.</summary>
    public void Start()
    {
        // Sessions left open by a crashed or killed previous process would read as active to the CLI.
        log.CloseDanglingEntries(ActivityLogEndedReasons.AppTerminated);

        if (Settings.ActivateOnLaunch) { Activate(DefaultInterval); }

        ReconcileAllTriggers();
    }

    /// <summary>Call on exit: ends the session and closes its log entry.</summary>
    public void Shutdown()
    {
        if (disposed) { return; }
        sessionGeneration++; // the ending session must not post notifications or request quit
        if (timer.IsScheduled)
        {
            ownership.Terminate();
            timer.Invalidate();
            log.RecordEnded(ActivityLogEndedReasons.AppTerminated);
        }
        var disks = driveAlive;
        TearDownSessionSideEffects();
        // Let the ping-file removal finish before the process exits.
        disks?.Flush(TimeSpan.FromSeconds(3));
        Dispose();
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        store.Changed -= OnSettingsChanged;
        platform.PowerSource.Changed -= OnPowerChanged;
        platform.DisplayMonitor.Changed -= OnDisplaysChanged;
        platform.SessionEvents.SessionBecameActive -= OnSessionBecameActive;
        platform.SessionEvents.SessionResignedActive -= OnSessionResignedActive;
        wifiMonitor.Dispose();
        appMonitor.Dispose();
        audioMonitor.Dispose();
        downloadMonitor.Dispose();
        scheduleMonitor.Dispose();
        cpuMonitor.Dispose();
        jiggler?.Dispose();
        driveAlive?.Dispose();
    }

    #region User actions

    private TimeSpan? DefaultInterval => Durations.Default.TimeSpan;

    /// <summary>Tray icon click: ends the running session or starts one with the default duration.</summary>
    public void Toggle()
    {
        if (timer.IsScheduled) { Deactivate(); }
        else { Activate(DefaultInterval); }
    }

    /// <summary>Starts a user session; null or zero means indefinite. Replaces a running session.</summary>
    public void Activate(TimeSpan? interval) => Activate(interval, ActivationSource.User);

    public void Activate(ActivationDuration duration) => Activate(duration.TimeSpan);

    public void Deactivate()
    {
        ownership.Terminate();
        TerminateTimer(ActivityLogEndedReasons.UserCancelled);
    }

    public void Handle(AppCommand command)
    {
        switch (command.Kind)
        {
            case AppCommandKind.Activate:
                Activate(command.UseDefaultDuration ? DefaultInterval : command.Duration);
                break;
            case AppCommandKind.Deactivate:
                Deactivate();
                break;
            case AppCommandKind.Toggle:
                Toggle();
                break;
        }
    }

    #endregion

    #region Session lifecycle

    /// <summary>Indefinite for null / zero; null result for negative or longer-than-allowed intervals.</summary>
    internal static bool TryNormalizeInterval(TimeSpan? interval, out TimeSpan? normalized)
    {
        normalized = interval == TimeSpan.Zero ? null : interval;
        return interval is not { } i || (i >= TimeSpan.Zero && i.TotalSeconds <= ActivationDuration.MaximumSeconds);
    }

    private void Activate(TimeSpan? requested, ActivationSource source)
    {
        if (!TryNormalizeInterval(requested, out var interval)) { return; }

        // Bumped first: the replaced session's end callback (raised by TerminateTimer
        // below) must not tear down side effects or announce "allowing sleep".
        var generation = ++sessionGeneration;

        // Replacing a running session: end it properly first so its log entry closes.
        if (timer.IsScheduled)
        {
            ownership.Terminate();
            TerminateTimer(ActivityLogEndedReasons.UserCancelled);
        }

        ownership.Start(source);
        var notifications = Settings.ShowNotifications;
        timer.Schedule(interval, Settings.AllowDisplaySleep, cancelled => OnSessionEnded(generation, cancelled));
        log.RecordStarted(source.ToLogString(), interval?.TotalSeconds ?? -1);
        if (notifications) { NotificationRequested?.Invoke(this, new SessionNotification(true, timer.FireDate)); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Feature triggers start an indefinite session only when nothing is running.</summary>
    private void ActivateForTrigger(ActivationSource source)
    {
        if (timer.IsScheduled) { return; }
        Activate(null, source);
    }

    private void OnSessionWillActivate()
    {
        // Started below the threshold by the user: they explicitly want to stay awake anyway.
        // Trigger sessions never get this exemption.
        batteryOverride = ownership is { IsActive: true, Source: ActivationSource.User }
                          && platform.PowerSource.Current is { State: not BatteryState.Unknown } p
                          && p.CapacityPercent <= Settings.EffectiveBatteryThreshold;
        ReconcileSessionSideEffects(force: true);
    }

    private void OnSessionEnded(int generation, bool cancelled)
    {
        // A replaced session ending must not tear down the newer session's side effects.
        if (generation != sessionGeneration) { return; }
        ownership.Terminate();
        TearDownSessionSideEffects();

        if (!cancelled) { log.RecordEnded(ActivityLogEndedReasons.Expired); }
        if (Settings.ShowNotifications) { NotificationRequested?.Invoke(this, new SessionNotification(false, null)); }
        StateChanged?.Invoke(this, EventArgs.Empty);

        if (!cancelled && Settings.QuitOnTimerExpiration) { QuitRequested?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>Ends the session and logs <paramref name="reason"/>. Callers clear ownership first.</summary>
    private void TerminateTimer(string reason)
    {
        batteryOverride = false;
        if (!timer.IsScheduled) { return; }
        timer.Invalidate();
        log.RecordEnded(reason);
    }

    /// <summary>Ends the running session only when <paramref name="source"/> started it.</summary>
    private void TerminateIfOwnedBy(ActivationSource source)
    {
        // The trigger ended while its session was suspended for user switching: don't resume it.
        if (source != ActivationSource.User && suspendedSession is { } suspended && suspended.Source == source)
        {
            suspendedSession = null;
        }

        if (!timer.IsScheduled) { return; }
        if (!ownership.TerminateIfOwnedBy(source)) { return; }
        TerminateTimer(ActivityLogEndedReasons.TriggerCancelled);
    }

    #endregion

    #region Session side effects (mouse jiggler, drive alive)

    /// <summary>Applies Mouse Jiggler / Drive Alive / Prevent disk sleep toggles to the running session.</summary>
    private void ReconcileSessionSideEffects(bool force = false)
    {
        if (!timer.IsScheduled && !force) { return; }
        ReconcileJiggler(Settings.MouseJiggler);
        ReconcileDiskKeepAlive(Settings.DriveAlive, Settings.PreventDiskSleep);
        ReconcileDisplaySleep(Settings.AllowDisplaySleep);
    }

    private void ReconcileJiggler(bool enabled)
    {
        if (enabled)
        {
            jiggler ??= new MouseJiggler(platform.Scheduler, platform.PointerNudger);
            jiggler.Start();
            return;
        }
        jiggler?.Stop();
        jiggler = null;
    }

    /// <summary>Prevent disk sleep pings the system drive; Drive Alive also pings the other drives.</summary>
    private void ReconcileDiskKeepAlive(bool otherDrives, bool systemDrive)
    {
        var wanted = otherDrives || systemDrive;
        if (driveAlive is not null && (!wanted || driveAlive.IncludesExternalDrives != otherDrives))
        {
            driveAlive.Stop();
            driveAlive = null;
        }
        if (wanted && driveAlive is null)
        {
            driveAlive = new DriveAliveTimer(platform.Scheduler, otherDrives ? platform.DriveEnumerator : null);
            driveAlive.Start();
        }
    }

    private void ReconcileDisplaySleep(bool allowDisplaySleep)
    {
        if (allowDisplaySleep != lastAllowDisplaySleep) { timer.Reacquire(allowDisplaySleep); }
        lastAllowDisplaySleep = allowDisplaySleep;
    }

    private void TearDownSessionSideEffects()
    {
        jiggler?.Stop();
        jiggler = null;
        driveAlive?.Stop();
        driveAlive = null;
    }

    #endregion

    #region Triggers

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (disposed) { return; }
        ReconcileSessionSideEffects();
        ReconcileAllTriggers();
    }

    private void ReconcileAllTriggers()
    {
        ReconcileWiFiTrigger();
        ReconcileApplicationTrigger();
        ReconcileACPowerTrigger();
        ReconcileDisplayTrigger();
        ReconcileScheduleTrigger();
        ReconcileDownloadTrigger();
        ReconcileAudioTrigger();
        ReconcileCpuTrigger();
    }

    /// <summary>
    /// Re-evaluates the Wi-Fi trigger only when the watched list changed, so unrelated
    /// settings changes can't restart a session the user just ended.
    /// </summary>
    private void ReconcileWiFiTrigger()
    {
        var ssids = Settings.EnabledWatchedWiFiSsids();
        if (reconciledSsids is not null && reconciledSsids.SequenceEqual(ssids)) { return; }
        reconciledSsids = ssids;
        RestartListMonitor(wifiMonitor, ssids.Count > 0, ActivationSource.WatchedSsid);
    }

    private void ReconcileApplicationTrigger()
    {
        var apps = Settings.EnabledWatchedApplications();
        if (reconciledApps is not null && reconciledApps.SequenceEqual(apps, StringComparer.OrdinalIgnoreCase)) { return; }
        reconciledApps = apps;
        RestartListMonitor(appMonitor, apps.Count > 0, ActivationSource.WatchedApp);
    }

    /// <summary>
    /// Restarts a list-driven monitor so the new list is evaluated from scratch: a watched
    /// item that already matches starts a session; when nothing matches any more the
    /// session the trigger started ends.
    /// </summary>
    private void RestartListMonitor(PollingConditionMonitor monitor, bool enabled, ActivationSource source)
    {
        var wasMatching = monitor.State;
        monitor.Stop();
        if (!enabled)
        {
            TerminateIfOwnedBy(source);
            return;
        }
        monitor.Start();
        if (wasMatching && !monitor.State) { TerminateIfOwnedBy(source); }
    }

    private void ReconcileACPowerTrigger()
    {
        if (Settings.ActivateOnACPower && !acTriggerEngaged)
        {
            acTriggerEngaged = true;
            acTriggerWasOnAC = false; // the first observation counts as a transition
            EvaluateACPower();
        }
        else if (!Settings.ActivateOnACPower && acTriggerEngaged)
        {
            acTriggerEngaged = false;
            acTriggerWasOnAC = false;
            TerminateIfOwnedBy(ActivationSource.ACPower);
        }
    }

    private void EvaluateACPower()
    {
        if (!acTriggerEngaged) { return; }
        var state = platform.PowerSource.Current.State;
        if (state == BatteryState.Unknown) { return; } // desktop PCs: nothing to act on

        var onAC = state is BatteryState.Charging or BatteryState.Full;
        var wasOnAC = acTriggerWasOnAC;
        acTriggerWasOnAC = onAC;

        // Only on transitions: otherwise a session the user ended on AC would come right back.
        if (onAC && !wasOnAC) { ActivateForTrigger(ActivationSource.ACPower); }
        else if (!onAC && wasOnAC) { TerminateIfOwnedBy(ActivationSource.ACPower); }
    }

    private void ReconcileScheduleTrigger()
    {
        var windows = Settings.EnabledScheduleWindows();
        if (!Settings.ScheduleEnabled || windows.Count == 0)
        {
            scheduleMonitor.Stop();
            TerminateIfOwnedBy(ActivationSource.Schedule);
            return;
        }
        scheduleMonitor.SetWindows(windows);
        scheduleMonitor.Start();
    }

    private void ReconcileDownloadTrigger()
    {
        var dirs = Settings.EnabledDownloadDirectories();
        if (!Settings.DownloadInProgressActivationEnabled || dirs.Count == 0)
        {
            downloadMonitor.Stop();
            TerminateIfOwnedBy(ActivationSource.Download);
            return;
        }
        var wasMatching = downloadMonitor.State;
        downloadMonitor.SetDirectories(dirs);
        downloadMonitor.Start();
        if (wasMatching && !downloadMonitor.State) { TerminateIfOwnedBy(ActivationSource.Download); }
    }

    private void ReconcileAudioTrigger()
    {
        if (!Settings.ActivateOnExternalAudioOutput)
        {
            audioMonitor.Stop();
            TerminateIfOwnedBy(ActivationSource.AudioOutput);
            return;
        }
        audioMonitor.Start();
    }

    private void ReconcileCpuTrigger()
    {
        if (!Settings.ActivateOnCpuLoad)
        {
            cpuMonitor.Stop();
            TerminateIfOwnedBy(ActivationSource.CpuLoad);
            return;
        }
        cpuMonitor.ThresholdPercent = Settings.EffectiveCpuLoadThreshold;
        cpuMonitor.Start();
    }

    /// <summary>Lets the platform nudge an immediate Wi-Fi re-check (network change notification).</summary>
    public void NetworkChanged() => wifiMonitor.Evaluate();

    #endregion

    #region Power, displays and user switching

    private void OnPowerChanged(object? sender, EventArgs e)
    {
        EvaluateACPower();
        if (!timer.IsScheduled) { return; }

        var s = Settings;
        var power = platform.PowerSource.Current;

        if (s.DeactivateOnFullCharge && power.State == BatteryState.Full)
        {
            Deactivate();
            return;
        }
        if (s.BatterySaverMonitoringEnabled && power.BatterySaverEnabled)
        {
            Deactivate();
            return;
        }
        if (s.BatteryCapacityThresholdEnabled && power.State == BatteryState.Unplugged
            && power.CapacityPercent <= s.EffectiveBatteryThreshold && !batteryOverride)
        {
            Deactivate();
        }
    }

    private void ReconcileDisplayTrigger()
    {
        if (Settings.ActivateOnExternalDisplay && !displayTriggerEngaged)
        {
            displayTriggerEngaged = true;
            OnDisplaysChanged(this, EventArgs.Empty); // a display may already be connected
        }
        else if (!Settings.ActivateOnExternalDisplay && displayTriggerEngaged)
        {
            displayTriggerEngaged = false;
            TerminateIfOwnedBy(ActivationSource.ExternalDisplay);
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        if (!displayTriggerEngaged) { return; }
        if (platform.DisplayMonitor.ExternalDisplayCount == 0)
        {
            TerminateIfOwnedBy(ActivationSource.ExternalDisplay);
        }
        else
        {
            ActivateForTrigger(ActivationSource.ExternalDisplay);
        }
    }

    private void OnSessionResignedActive(object? sender, EventArgs e)
    {
        if (!Settings.DeactivateOnUserSwitch || !timer.IsScheduled) { return; }
        suspendedSession = (timer.ScheduledInterval, ownership.IsActive ? ownership.Source : ActivationSource.User);
        Deactivate();
    }

    private void OnSessionBecameActive(object? sender, EventArgs e)
    {
        if (!Settings.DeactivateOnUserSwitch || suspendedSession is not { } suspended) { return; }
        suspendedSession = null;
        Activate(suspended.Interval, suspended.Source);
    }

    #endregion
}
