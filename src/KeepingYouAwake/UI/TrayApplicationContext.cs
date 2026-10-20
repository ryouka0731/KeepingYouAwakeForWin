using System.Net.NetworkInformation;
using KeepingYouAwake.Core;
using KeepingYouAwake.Windows;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace KeepingYouAwake.UI;

/// <summary>Replaceable dependencies of <see cref="TrayApplicationContext"/>; the defaults are the real ones.</summary>
/// <param name="Platform">OS services; null for the Windows implementations.</param>
/// <param name="Shell">Registry integration; null for HKEY_CURRENT_USER.</param>
/// <param name="BackgroundUpdateChecks">Whether to start the daily GitHub check.</param>
/// <param name="UpdateHandler">HTTP handler for update checks; null for the network.</param>
/// <param name="Prompts">Modal dialogs of the Settings window; null for the real ones.</param>
internal sealed record TrayOptions(
    PlatformServices? Platform = null,
    ShellIntegration? Shell = null,
    bool BackgroundUpdateChecks = true,
    HttpMessageHandler? UpdateHandler = null,
    IUserPrompts? Prompts = null);

/// <summary>
/// The app's lifetime: tray icon, menu, notifications and windows around the
/// <see cref="AppController"/>. Left-click toggles; right-click opens the menu.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly SettingsStore store;
    private readonly ActivityLogger log;
    private readonly ShellIntegration shell;
    private readonly IUserPrompts? prompts;
    private readonly Core.Platform.IScheduler clock;
    private readonly UpdateService updates;
    private readonly List<IDisposable> services;
    private readonly NotifyIcon notifyIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly TrayIcons icons = new();
    private readonly Timer countdownTimer = new() { Interval = 1000 };
    private readonly Timer settingsReloadTimer = new() { Interval = 300 };
    private readonly FileSystemWatcher? settingsWatcher;
    private SettingsForm? settingsForm;
    private ActivityLogForm? activityLogForm;
    private string? pendingUpdateUrl;
    private bool exiting;

    public TrayApplicationContext(SettingsStore store, ActivityLogger log, TrayOptions? options = null)
    {
        options ??= new TrayOptions();
        this.store = store;
        this.log = log;
        shell = options.Shell ?? ShellIntegration.Current;
        prompts = options.Prompts;
        PlatformServices services;
        (services, this.services) = options.Platform is null ? CreateWindowsPlatform() : (options.Platform, []);

        clock = services.Scheduler;
        Controller = new AppController(services, store, log);
        Controller.StateChanged += (_, _) => UpdateTrayState();
        Controller.NotificationRequested += (_, n) => ShowSessionNotification(n);
        Controller.QuitRequested += (_, _) => Quit();

        notifyIcon = new NotifyIcon { Icon = icons.Inactive, ContextMenuStrip = menu, Visible = true };
        notifyIcon.MouseClick += OnNotifyIconClick;
        notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
        menu.Opening += (_, _) => RebuildMenu();

        countdownTimer.Tick += (_, _) => UpdateTooltip();
        store.Changed += OnSettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.SessionEnding += OnSessionEnding;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        settingsWatcher = WatchSettingsFile();

        updates = new UpdateService(store, options.UpdateHandler);
        updates.UpdateFound += (_, release) => ShowUpdateNotification(release);

        Controller.Start();
        if (options.BackgroundUpdateChecks) { updates.StartBackgroundChecks(); }
        UpdateTrayState();
    }

    internal AppController Controller { get; }
    internal ContextMenuStrip Menu => menu;
    internal string TooltipText => notifyIcon.Text;
    internal bool IsExiting => exiting;
    internal (string Title, string Body)? LastNotification { get; private set; }

    private static (PlatformServices, List<IDisposable>) CreateWindowsPlatform()
    {
        var scheduler = new WinFormsScheduler();
        var assertion = new PowerRequestAssertion();
        var power = new WindowsPowerSource(scheduler);
        var sessions = new WindowsSessionEvents();
        var displays = new DisplayConfigMonitor();
        var platform = new PlatformServices(
            scheduler, assertion, power, sessions, displays,
            new WlanWiFiProbe(), new SessionProcessLister(), new CoreAudioProbe(),
            new SystemTimesCpuSampler(), new SendInputNudger(), new WindowsDriveEnumerator());
        return (platform, [assertion, power, sessions, displays]);
    }

    /// <summary>Handles arguments from the command line or a second instance.</summary>
    public void Handle(string[] args)
    {
        switch (AppCommand.ParseArguments(args))
        {
            case null:
                return;
            case { Kind: AppCommandKind.ShowSettings }:
                ShowSettings();
                break;
            case { Kind: AppCommandKind.Quit }:
                Quit();
                break;
            case var command:
                Controller.Handle(command);
                break;
        }
    }

    #region Tray state

    private void OnSettingsChanged(object? sender, EventArgs e) => UpdateTrayState();

    private TrayState State => TrayState.From(Controller, store.Settings);

    private void UpdateTrayState()
    {
        if (exiting) { return; }
        notifyIcon.Icon = Controller.IsActive ? icons.Active : icons.Inactive;
        countdownTimer.Enabled = State.ShowsCountdown;
        UpdateTooltip();
    }

    private void UpdateTooltip() => notifyIcon.Text = TrayPresenter.Tooltip(State, clock.Now);

    private void ShowSessionNotification(SessionNotification notification)
    {
        var (title, body) = TrayPresenter.Notification(notification, clock.Now);
        pendingUpdateUrl = null;
        LastNotification = (title, body);
        notifyIcon.ShowBalloonTip(3000, title, body, ToolTipIcon.None);
    }

    private void ShowUpdateNotification(ReleaseInfo release)
    {
        var title = Strings.Get("UpdateAvailableTitle");
        var body = Strings.Format("UpdateAvailableFormat", release.Tag, UpdateService.CurrentVersion);
        pendingUpdateUrl = release.PageUrl;
        LastNotification = (title, body);
        notifyIcon.ShowBalloonTip(10_000, title, body, ToolTipIcon.Info);
    }

    /// <summary>Left-click toggles; right-click opens the context menu (handled by the NotifyIcon).</summary>
    internal void OnNotifyIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { Controller.Toggle(); }
    }

    /// <summary>Clicking an update notification opens the release page.</summary>
    internal void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (pendingUpdateUrl is { } url) { (prompts ?? WinFormsPrompts.Instance).OpenUrl(url); }
    }

    internal UpdateService Updates => updates;
    internal string? PendingUpdateUrl => pendingUpdateUrl;

    internal void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
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
    internal void OnSessionEnding(object sender, SessionEndingEventArgs e) => UiThread.Post(Quit);

    internal void OnNetworkAddressChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        if (!exiting) { Controller.NetworkChanged(); }
    });

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

    #endregion

    #region Menu

    /// <summary>Renders <see cref="TrayPresenter.BuildMenu"/> into the context menu.</summary>
    internal void RebuildMenu()
    {
        // Clear() doesn't dispose; submenus that were shown keep window handles otherwise.
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToList()) { item.Dispose(); }
        menu.Items.Clear();
        menu.Items.AddRange(TrayPresenter.BuildMenu(State, clock.Now).Select(Render).ToArray());
    }

    private ToolStripItem Render(TrayMenuItem item)
    {
        if (item.IsSeparator) { return new ToolStripSeparator(); }
        var rendered = new ToolStripMenuItem(item.Title) { Enabled = item.Enabled, Checked = item.Checked, Tag = item };
        if (item.Children is { } children)
        {
            rendered.DropDownItems.AddRange(children.Select(Render).ToArray());
        }
        else if (item.Action != TrayMenuAction.None)
        {
            rendered.Click += (_, _) => Perform(item);
        }
        return rendered;
    }

    internal void Perform(TrayMenuItem item)
    {
        switch (item.Action)
        {
            case TrayMenuAction.Toggle: Controller.Toggle(); break;
            case TrayMenuAction.Deactivate: Controller.Deactivate(); break;
            case TrayMenuAction.ActivateDuration when item.Duration is { } d: Controller.Activate(d); break;
            case TrayMenuAction.ActivateUntilEndOfDay: Controller.Activate(TrayPresenter.UntilNextMidnight(clock.Now.LocalDateTime)); break;
            case TrayMenuAction.SetDefaultDuration when item.Duration is { } d: Controller.Durations.Default = d; break;
            case TrayMenuAction.ShowSettings: ShowSettings(); break;
            case TrayMenuAction.ShowActivityLog: ShowActivityLog(); break;
            case TrayMenuAction.Quit: Quit(); break;
        }
    }

    #endregion

    #region Windows

    internal SettingsForm ShowSettings()
    {
        if (settingsForm is null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(store, Controller.Durations, updates, shell, prompts);
        }
        settingsForm.ShowAndActivate();
        return settingsForm;
    }

    internal ActivityLogForm ShowActivityLog()
    {
        if (activityLogForm is null || activityLogForm.IsDisposed)
        {
            activityLogForm = new ActivityLogForm(log);
        }
        activityLogForm.ShowAndActivate();
        return activityLogForm;
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
        store.Changed -= OnSettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.SessionEnding -= OnSessionEnding;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        countdownTimer.Stop();
        settingsWatcher?.Dispose();
        settingsReloadTimer.Stop();
        Controller.Shutdown();
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
