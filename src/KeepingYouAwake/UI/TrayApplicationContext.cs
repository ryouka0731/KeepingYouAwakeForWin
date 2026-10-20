using System.Net.NetworkInformation;
using KeepingYouAwake.Core;
using KeepingYouAwake.Windows;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace KeepingYouAwake.UI;

/// <summary>
/// The app's lifetime: tray icon, menu, notifications and windows around the
/// <see cref="AppController"/>. Left-click toggles; right-click opens the menu.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore store;
    private readonly AppController controller;
    private readonly ActivityLogger log;
    private readonly UpdateService updates;
    private readonly List<IDisposable> services = [];
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly TrayIcons icons = new();
    private readonly Timer countdownTimer = new() { Interval = 1000 };
    private SettingsForm? settingsForm;
    private ActivityLogForm? activityLogForm;
    private readonly FileSystemWatcher? settingsWatcher;
    private readonly Timer settingsReloadTimer = new() { Interval = 300 };
    private string? pendingUpdateUrl;
    private bool exiting;

    public TrayApplicationContext(SettingsStore store, ActivityLogger log)
    {
        this.store = store;
        this.log = log;

        var scheduler = new WinFormsScheduler();
        var assertion = new PowerRequestAssertion();
        var power = new WindowsPowerSource(scheduler);
        var sessions = new WindowsSessionEvents();
        var displays = new DisplayConfigMonitor();
        services.AddRange([assertion, power, sessions, displays]);

        controller = new AppController(new PlatformServices(
            scheduler, assertion, power, sessions, displays,
            new WlanWiFiProbe(), new SessionProcessLister(), new CoreAudioProbe(),
            new SystemTimesCpuSampler(), new SendInputNudger(), new WindowsDriveEnumerator()), store, log);
        controller.StateChanged += (_, _) => UpdateTrayState();
        controller.NotificationRequested += (_, n) => ShowSessionNotification(n);
        controller.QuitRequested += (_, _) => Quit();

        notifyIcon = new NotifyIcon
        {
            Icon = icons.Inactive,
            Text = Strings.Get("TooltipInactive"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) { controller.Toggle(); }
        };
        notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (pendingUpdateUrl is { } url) { ShellIntegration.OpenUrl(url); }
        };
        menu.Opening += (_, _) => BuildMenu();

        countdownTimer.Tick += (_, _) => UpdateTooltip();
        store.Changed += (_, _) => UpdateTrayState();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        settingsWatcher = WatchSettingsFile();

        updates = new UpdateService(store);
        updates.UpdateFound += (_, release) =>
        {
            pendingUpdateUrl = release.PageUrl;
            notifyIcon.ShowBalloonTip(10_000, Strings.Get("UpdateAvailableTitle"),
                Strings.Format("UpdateAvailableFormat", release.Tag, UpdateService.CurrentVersion), ToolTipIcon.Info);
        };

        controller.Start();
        updates.StartBackgroundChecks();
        UpdateTrayState();
    }

    public void Handle(string[] args)
    {
        if (AppCommand.ParseArguments(args) is not { } command) { return; }
        if (command.Kind == AppCommandKind.ShowSettings) { ShowSettings(); }
        else { controller.Handle(command); }
    }

    /// <summary>Applies outside edits of settings.json (e.g. the MCP server's kya_set_default) without a restart.</summary>
    private FileSystemWatcher? WatchSettingsFile()
    {
        if (store.FilePath is not { } path || Path.GetDirectoryName(path) is not { } directory) { return null; }
        try
        {
            Directory.CreateDirectory(directory);
            var watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            // Debounced: editors and atomic renames raise several events per save.
            void OnEvent(object sender, FileSystemEventArgs e) => UiThread.Post(() =>
            {
                if (exiting) { return; }
                settingsReloadTimer.Stop();
                settingsReloadTimer.Start();
            });
            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Renamed += (s, e) => OnEvent(s, e);
            settingsReloadTimer.Tick += (_, _) =>
            {
                settingsReloadTimer.Stop();
                store.ReloadFromDisk();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    #region Tray state

    private void UpdateTrayState()
    {
        if (exiting) { return; }
        notifyIcon.Icon = controller.IsActive ? icons.Active : icons.Inactive;
        var countdown = controller.IsActive && controller.FireDate is not null && !store.Settings.CountdownHidden;
        countdownTimer.Enabled = countdown;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        string text;
        if (!controller.IsActive) { text = Strings.Get("TooltipInactive"); }
        else if (controller.FireDate is { } fireDate && !store.Settings.CountdownHidden)
        {
            text = Strings.Format("TooltipActiveRemaining", Strings.Countdown(fireDate - DateTimeOffset.Now));
        }
        else { text = Strings.Get("TooltipActive"); }
        notifyIcon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void ShowSessionNotification(SessionNotification notification)
    {
        string title, body;
        if (!notification.Activating)
        {
            title = Strings.Get("AllowingSleepTitle");
            body = Strings.Get("AllowingSleepBody");
        }
        else if (notification.FireDate is { } fireDate)
        {
            var remaining = TimeSpan.FromSeconds(Math.Round((fireDate - DateTimeOffset.Now).TotalSeconds));
            title = Strings.Format("PreventingSleepForTitle", Strings.DurationText(remaining));
            body = Strings.Get("PreventingSleepForBody");
        }
        else
        {
            title = Strings.Get("PreventingSleepTitle");
            body = Strings.Get("PreventingSleepBody");
        }
        pendingUpdateUrl = null;
        notifyIcon.ShowBalloonTip(3000, title, body, ToolTipIcon.None);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) { return; }
        UiThread.Post(() =>
        {
            if (exiting) { return; }
            icons.Rebuild();
            UpdateTrayState();
            icons.ReleaseStale();
        });
    }

    /// <summary>Sign-out / shutdown: clean up (log entry, drive ping files) like a normal quit.</summary>
    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => UiThread.Post(Quit);

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (!exiting) { controller.NetworkChanged(); }
    });

    #endregion

    #region Menu

    private void BuildMenu()
    {
        // Clear() doesn't dispose; submenus that were shown keep window handles otherwise.
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList()) { item.Dispose(); }
        menu.Items.Clear();

        if (controller.IsActive)
        {
            if (controller.FireDate is { } fireDate)
            {
                menu.Items.Add(new ToolStripMenuItem(Strings.Format("RemainingFormat", Strings.Countdown(fireDate - DateTimeOffset.Now))) { Enabled = false });
            }
            if (controller.CurrentSource is { } source && source != ActivationSource.User)
            {
                menu.Items.Add(new ToolStripMenuItem(Strings.Format("StartedByFormat", Strings.SourceName(source.ToLogString()))) { Enabled = false });
            }
            menu.Items.Add(new ToolStripMenuItem(Strings.Get("Deactivate"), null, (_, _) => controller.Deactivate()));
        }
        else
        {
            var defaultTitle = Strings.DurationTitle(controller.Durations.Default);
            menu.Items.Add(new ToolStripMenuItem($"{Strings.Get("Activate")} ({defaultTitle})", null, (_, _) => controller.Toggle()));
        }
        menu.Items.Add(new ToolStripSeparator());

        var activateFor = new ToolStripMenuItem(Strings.Get("ActivateForDuration"));
        var setDefault = new ToolStripMenuItem(Strings.Get("SetDefaultDuration"));
        var current = controller.CurrentDuration;
        var defaultDuration = controller.Durations.Default;
        foreach (var duration in controller.Durations.Durations)
        {
            var title = Strings.DurationTitle(duration);
            if (duration == defaultDuration) { title += " " + Strings.Get("IsDefaultSuffix"); }
            var selected = duration;
            activateFor.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => controller.Activate(selected))
            {
                Checked = current == duration,
            });
            setDefault.DropDownItems.Add(new ToolStripMenuItem(Strings.DurationTitle(duration), null, (_, _) => controller.Durations.Default = selected)
            {
                Checked = duration == defaultDuration,
            });
        }

        var untilMidnight = SecondsUntilNextMidnight();
        if (untilMidnight > 0)
        {
            activateFor.DropDownItems.Add(new ToolStripSeparator());
            activateFor.DropDownItems.Add(new ToolStripMenuItem(Strings.Get("UntilEndOfDay"), null,
                (_, _) => controller.Activate(TimeSpan.FromSeconds(SecondsUntilNextMidnight()))));
        }

        menu.Items.Add(activateFor);
        menu.Items.Add(setDefault);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.Get("SettingsEllipsis"), null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem(Strings.Get("ShowActivityLogEllipsis"), null, (_, _) => ShowActivityLog()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.Get("Quit"), null, (_, _) => Quit()));
    }

    /// <summary>Whole seconds until the next local midnight, rounded up so it is never 0 (= indefinite).</summary>
    private static double SecondsUntilNextMidnight()
    {
        var now = DateTime.Now;
        return Math.Ceiling((now.Date.AddDays(1) - now).TotalSeconds);
    }

    #endregion

    #region Windows

    private void ShowSettings()
    {
        if (settingsForm is null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(store, controller.Durations, updates);
        }
        settingsForm.ShowAndActivate();
    }

    private void ShowActivityLog()
    {
        if (activityLogForm is null || activityLogForm.IsDisposed)
        {
            activityLogForm = new ActivityLogForm(log);
        }
        activityLogForm.ShowAndActivate();
    }

    #endregion

    private void Quit()
    {
        if (exiting) { return; }
        exiting = true;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        countdownTimer.Stop();
        settingsWatcher?.Dispose();
        settingsReloadTimer.Stop();
        controller.Shutdown();
        updates.Dispose();
        foreach (var service in services) { service.Dispose(); }
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        settingsForm?.Close();
        activityLogForm?.Close();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            countdownTimer.Dispose();
            settingsReloadTimer.Dispose();
            menu.Dispose();
            icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
