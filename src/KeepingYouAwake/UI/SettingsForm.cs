using KeepingYouAwake.Core;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.UI;

/// <summary>
/// The Settings window. Every control writes straight to <see cref="SettingsStore"/>; changes apply
/// immediately. Controls get a <see cref="Control.Name"/> so component tests can find them.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly SettingsStore store;
    private readonly ActivationDurationsController durations;
    private readonly UpdateService updates;
    private readonly ShellIntegration shell;
    private readonly IUserPrompts prompts;
    private readonly List<Action> refreshers = [];
    /// <summary>The + buttons' popup menu, reused and disposed with the window.</summary>
    private readonly ContextMenuStrip popup = new();

    internal ContextMenuStrip Popup => popup;
    private bool refreshing;

    public SettingsForm(SettingsStore store, ActivationDurationsController durations, UpdateService updates,
        ShellIntegration shell, IUserPrompts? prompts = null)
    {
        this.store = store;
        this.durations = durations;
        this.updates = updates;
        this.shell = shell;
        this.prompts = prompts ?? WinFormsPrompts.Instance;

        Text = Strings.Get("SettingsTitle");
        Icon = TrayIcons.AppIcon();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        ClientSize = new Size(560, 600);
        MinimumSize = new Size(480, 400);
        ShowInTaskbar = true;

        Tabs = new TabControl { Dock = DockStyle.Fill, Name = "Tabs" };
        Tabs.TabPages.AddRange(
        [
            Ui.Page(Strings.Get("TabGeneral"), BuildGeneral()),
            Ui.Page(Strings.Get("TabDurations"), BuildDurations()),
            Ui.Page(Strings.Get("TabBattery"), BuildBattery()),
            Ui.Page(Strings.Get("TabWatchedItems"), BuildWatchedItems()),
            Ui.Page(Strings.Get("TabAdvanced"), BuildAdvanced()),
            Ui.Page(Strings.Get("TabUpdates"), BuildUpdates()),
            Ui.Page(Strings.Get("TabAbout"), BuildAbout()),
        ]);
        Controls.Add(Tabs);

        store.Changed += OnStoreChanged;
        FormClosed += (_, _) => store.Changed -= OnStoreChanged;
        RefreshAll();
    }

    internal TabControl Tabs { get; }

    /// <summary>Finds a named control anywhere in the window (component tests).</summary>
    internal T Find<T>(string name) where T : Control =>
        Controls.Find(name, searchAllChildren: true).OfType<T>().Single();

    private void OnStoreChanged(object? sender, EventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        refreshing = true;
        try { foreach (var refresh in refreshers) { refresh(); } }
        finally { refreshing = false; }
    }

    private void Update(Action<AppSettings> change)
    {
        if (!refreshing) { store.Update(change); }
    }

    /// <summary>A checkbox bound to a boolean setting.</summary>
    private CheckBox Bound(string name, string label, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var box = Ui.Check(label);
        box.Name = name;
        box.CheckedChanged += (_, _) => Update(s => set(s, box.Checked));
        refreshers.Add(() => box.Checked = get(store.Settings));
        return box;
    }

    #region General

    private Control BuildGeneral()
    {
        var column = Ui.Column();

        var startAtLogin = Ui.Check(Strings.Get("StartAtLogin"));
        startAtLogin.Name = "StartAtLogin";
        startAtLogin.Checked = shell.IsStartAtLoginEnabled;
        startAtLogin.CheckedChanged += (_, _) =>
        {
            try { shell.SetStartAtLogin(startAtLogin.Checked); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                prompts.Inform(this, ex.Message, Strings.Get("AppName"), MessageBoxIcon.Warning);
            }
            startAtLogin.Checked = shell.IsStartAtLoginEnabled;
        };
        column.Controls.Add(startAtLogin);
        column.Controls.Add(Ui.Hint(Strings.Get("StartAtLoginHint")));

        column.Controls.Add(Bound("ActivateOnLaunch", Strings.Get("ActivateOnLaunch"), s => s.ActivateOnLaunch, (s, v) => s.ActivateOnLaunch = v));
        column.Controls.Add(Ui.Hint(Strings.Get("ActivateOnLaunchHint")));
        column.Controls.Add(Bound("ShowNotifications", Strings.Get("ShowNotifications"), s => s.ShowNotifications, (s, v) => s.ShowNotifications = v));
        column.Controls.Add(Ui.Text(string.Empty));
        column.Controls.Add(Ui.Text(Strings.Get("LeftClickHint")));
        return column;
    }

    #endregion

    #region Durations

    private Control BuildDurations()
    {
        var column = Ui.Column();
        column.Controls.Add(Ui.Text(Strings.Get("DurationsHint")));
        var list = new ListBox { Name = "Durations", Width = 320, Height = 240, IntegralHeight = false, Margin = new Padding(0, 6, 0, 6) };
        column.Controls.Add(list);

        ActivationDuration? Selected() =>
            list.SelectedIndex >= 0 && list.SelectedIndex < durations.Durations.Count ? durations.Durations[list.SelectedIndex] : null;

        var remove = Ui.Button(Strings.Get("Remove"), (_, _) => { if (Selected() is { } d) { durations.Remove(d); } });
        var setDefault = Ui.Button(Strings.Get("SetDefault"), (_, _) => { if (Selected() is { } d) { durations.Default = d; } });
        remove.Name = "RemoveDuration";
        setDefault.Name = "SetDefaultDuration";
        var buttons = Ui.Row();
        var add = Ui.Button(Strings.Get("Add"), (_, _) => AddDuration());
        add.Name = "AddDuration";
        var reset = Ui.Button(Strings.Get("ResetEllipsis"), (_, _) => ResetDurations());
        reset.Name = "ResetDurations";
        buttons.Controls.AddRange([add, remove, setDefault, reset]);
        column.Controls.Add(buttons);

        void UpdateButtons()
        {
            var selected = Selected();
            remove.Enabled = selected is { IsIndefinite: false };
            setDefault.Enabled = selected is not null && selected != durations.Default;
        }
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        refreshers.Add(() =>
        {
            RefreshDurationList(list);
            UpdateButtons();
        });
        return column;
    }

    private void RefreshDurationList(ListBox list)
    {
        var index = list.SelectedIndex;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var d in durations.Durations)
        {
            var title = Strings.DurationTitle(d);
            list.Items.Add(d == durations.Default ? $"{title} {Strings.Get("IsDefaultSuffix")}" : title);
        }
        list.EndUpdate();
        if (index >= 0) { list.SelectedIndex = Math.Min(index, list.Items.Count - 1); }
    }

    private void AddDuration()
    {
        if (prompts.AskDuration(this) is not { } duration) { return; }
        if (!durations.Add(duration))
        {
            prompts.Inform(this, Strings.Get("DurationAlreadyAdded"), Strings.Get("AddDuration"), MessageBoxIcon.Information);
        }
    }

    private void ResetDurations()
    {
        if (prompts.Confirm(this, Strings.Get("ResetDurationsMessage"), Strings.Get("ResetDurationsTitle"))) { durations.Reset(); }
    }

    #endregion

    #region Battery

    private Control BuildBattery()
    {
        var column = Ui.Column();
        var status = Ui.Text(string.Empty);
        status.Name = "BatteryStatus";
        column.Controls.Add(status);

        column.Controls.Add(Bound("BatteryCapacityThresholdEnabled", Strings.Get("BatteryThreshold"),
            s => s.BatteryCapacityThresholdEnabled, (s, v) => s.BatteryCapacityThresholdEnabled = v));
        var row = Ui.Row();
        row.Margin = new Padding(20, 0, 0, 0);
        var slider = new TrackBar
        {
            Name = "BatteryThreshold",
            Minimum = (int)AppSettings.MinimumBatteryThreshold,
            Maximum = (int)AppSettings.MaximumBatteryThreshold,
            TickFrequency = 10,
            SmallChange = 5,
            LargeChange = 10,
            Width = 300,
        };
        var value = new Label { AutoSize = true, Margin = new Padding(6, 8, 0, 0) };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = $"{slider.Value} %";
            Update(s => s.BatteryCapacityThreshold = slider.Value);
        };
        row.Controls.AddRange([slider, value]);
        column.Controls.Add(row);
        column.Controls.Add(Ui.Hint(Strings.Get("BatteryThresholdHint")));

        column.Controls.Add(Bound("BatterySaverMonitoringEnabled", Strings.Get("BatterySaver"),
            s => s.BatterySaverMonitoringEnabled, (s, v) => s.BatterySaverMonitoringEnabled = v));
        column.Controls.Add(Ui.Hint(Strings.Get("BatterySaverHint")));
        column.Controls.Add(Bound("DeactivateOnFullCharge", Strings.Get("DeactivateOnFullCharge"),
            s => s.DeactivateOnFullCharge, (s, v) => s.DeactivateOnFullCharge = v));

        refreshers.Add(() =>
        {
            var settings = store.Settings;
            slider.Value = (int)Math.Round(settings.EffectiveBatteryThreshold);
            value.Text = $"{slider.Value} %";
            slider.Enabled = settings.BatteryCapacityThresholdEnabled;
            var power = WindowsPowerSource.Read();
            status.Text = power.State == Core.Platform.BatteryState.Unknown
                ? Strings.Get("NoBattery")
                : Strings.Format("BatteryStatusFormat", power.CapacityPercent);
        });
        return column;
    }

    #endregion

    #region Watched items

    private Control BuildWatchedItems()
    {
        var column = Ui.Column();
        BuildWiFiSection().AddTo(column);
        BuildApplicationsSection().AddTo(column);
        BuildDownloadsSection().AddTo(column);
        BuildScheduleSection(column);
        column.Controls.Add(Ui.Text(string.Empty));
        column.Controls.Add(Ui.Text(Strings.Get("WatchedItemToggleHint")));
        column.Controls.Add(Ui.Text(Strings.Get("TriggersNeedEnablingHint")));
        return column;
    }

    internal WatchedListSection WiFiSection { get; private set; } = null!;

    private WatchedListSection BuildWiFiSection()
    {
        var wifi = new WatchedListSection(this, "WatchedWiFi", Strings.Get("WatchedWiFi"), Strings.Get("WatchedWiFiHint"),
            new ListBinding(s => s.WatchedWiFiSsids, (s, v) => s.WatchedWiFiSsids = v,
                s => s.DisabledWatchedWiFiSsids, (s, v) => s.DisabledWatchedWiFiSsids = v));
        wifi.AddButton.Click += (_, _) => ShowMenu(wifi.AddButton, WiFiAddMenuItems(WlanWiFiProbe.Read(), wifi));
        WiFiSection = wifi;
        return wifi;
    }

    /// <summary>The Wi-Fi + menu: the joined networks, or why their names can't be read, plus manual entry.</summary>
    internal IEnumerable<ToolStripItem> WiFiAddMenuItems(WiFiReading reading, WatchedListSection wifi)
    {
        switch (reading.Status)
        {
            case WiFiReadStatus.Connected:
                foreach (var ssid in reading.Ssids)
                {
                    yield return new ToolStripMenuItem(Strings.Format("AddCurrentNetworkFormat", ssid), null, (_, _) => wifi.Add(ssid));
                }
                break;
            case WiFiReadStatus.AccessDenied:
                yield return new ToolStripMenuItem(Strings.Get("NetworkNameUnreadable")) { Enabled = false };
                yield return new ToolStripMenuItem(Strings.Get("OpenLocationSettings"), null,
                    (_, _) => prompts.OpenUrl("ms-settings:privacy-location"));
                break;
            default:
                yield return new ToolStripMenuItem(Strings.Get("NotConnectedToWiFi")) { Enabled = false };
                break;
        }
        yield return new ToolStripSeparator();
        yield return new ToolStripMenuItem(Strings.Get("EnterNetworkName"), null, (_, _) =>
        {
            if (prompts.AskText(this, Strings.Get("WatchedWiFi"), Strings.Get("NetworkName")) is { } name) { wifi.Add(name); }
        });
    }

    internal WatchedListSection ApplicationsSection { get; private set; } = null!;

    private WatchedListSection BuildApplicationsSection()
    {
        var apps = new WatchedListSection(this, "WatchedApplications", Strings.Get("WatchedApplications"), Strings.Get("WatchedApplicationsHint"),
            new ListBinding(s => s.WatchedApplications, (s, v) => s.WatchedApplications = v,
                s => s.DisabledWatchedApplications, (s, v) => s.DisabledWatchedApplications = v));
        apps.AddButton.Click += (_, _) => ShowMenu(apps.AddButton, ApplicationAddMenuItems(apps));
        ApplicationsSection = apps;
        return apps;
    }

    /// <summary>The applications + menu: pick a program file or a running app.</summary>
    internal IEnumerable<ToolStripItem> ApplicationAddMenuItems(WatchedListSection apps) =>
    [
        new ToolStripMenuItem(Strings.Get("ChooseApplicationFile"), null, (_, _) =>
        {
            var file = prompts.ChooseFile(this, Strings.Get("ChooseApplication"), Strings.Get("ApplicationFilter"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            if (file is not null) { apps.Add(WatchedItems.NormalizeExecutableName(file)); }
        }),
        new ToolStripMenuItem(Strings.Get("ChooseRunningApplication"), null, (_, _) =>
        {
            if (prompts.ChooseRunningApplication(this) is { } exe) { apps.Add(exe); }
        }),
    ];

    internal WatchedListSection DownloadsSection { get; private set; } = null!;

    private WatchedListSection BuildDownloadsSection()
    {
        var downloads = new WatchedListSection(this, "WatchedDownloadFolders", Strings.Get("WatchedDownloadFolders"), Strings.Get("WatchedDownloadFoldersHint"),
            new ListBinding(s => s.DownloadDirectories ?? [],
                // Removing the last folder goes back to watching the Downloads folder.
                (s, v) => s.DownloadDirectories = v.Count == 0 ? null : v,
                s => s.DisabledDownloadDirectories, (s, v) => s.DisabledDownloadDirectories = v));
        downloads.AddButton.Click += (_, _) =>
        {
            if (prompts.ChooseFolder(this, Strings.Get("ChooseFolder"), WatchedItems.DefaultDownloadsFolder) is { } folder)
            {
                downloads.Add(folder);
            }
        };
        DownloadsSection = downloads;
        return downloads;
    }

    private void BuildScheduleSection(Control column)
    {
        column.Controls.Add(Ui.Title(Strings.Get("ActiveHours")));
        column.Controls.Add(Ui.Hint(Strings.Get("ActiveHoursHint")));
        var schedule = new CheckedListBox { Name = "ActiveHours", Width = 460, Height = 110, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(20, 2, 0, 2) };
        schedule.ItemCheck += (_, e) =>
        {
            if (refreshing) { return; }
            var index = e.Index;
            var enabled = e.NewValue == CheckState.Checked;
            // ItemCheck fires before the check state changes; apply afterwards.
            BeginInvoke(() => Update(s => s.ScheduleWindows[index].Enabled = enabled));
        };
        column.Controls.Add(schedule);

        var buttons = Ui.Row();
        buttons.Margin = new Padding(20, 0, 0, 0);
        buttons.Controls.AddRange(
        [
            Named(Ui.Button(Strings.Get("Add"), (_, _) => EditScheduleWindow(null)), "AddActiveHours"),
            Named(Ui.Button(Strings.Get("Edit"), (_, _) => { if (schedule.SelectedIndex >= 0) { EditScheduleWindow(schedule.SelectedIndex); } }), "EditActiveHours"),
            Named(Ui.Button(Strings.Get("Remove"), (_, _) =>
            {
                var index = schedule.SelectedIndex;
                if (index >= 0) { Update(s => s.ScheduleWindows.RemoveAt(index)); }
            }), "RemoveActiveHours"),
        ]);
        column.Controls.Add(buttons);
        refreshers.Add(() =>
        {
            schedule.BeginUpdate();
            schedule.Items.Clear();
            foreach (var w in store.Settings.ScheduleWindows) { schedule.Items.Add(TrayPresenter.DescribeScheduleWindow(w), w.Enabled); }
            schedule.EndUpdate();
        });
    }

    private void EditScheduleWindow(int? index)
    {
        var edited = prompts.EditScheduleWindow(this, index is { } i ? store.Settings.ScheduleWindows[i] : null);
        if (edited is null) { return; }
        Update(s =>
        {
            if (index is { } at) { s.ScheduleWindows[at] = edited; }
            else { s.ScheduleWindows.Add(edited); }
        });
    }

    private static T Named<T>(T control, string name) where T : Control
    {
        control.Name = name;
        return control;
    }

    /// <summary>Shows the popup menu below <paramref name="anchor"/>, replacing (and disposing) its previous items.</summary>
    private void ShowMenu(Control anchor, IEnumerable<ToolStripItem> items)
    {
        foreach (var old in popup.Items.Cast<ToolStripItem>().ToList()) { old.Dispose(); }
        popup.Items.Clear();
        popup.Items.AddRange(items.ToArray());
        popup.Show(anchor, new Point(0, anchor.Height));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { popup.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>How a watched list maps to its item list and its switched-off list in the settings.</summary>
    internal sealed record ListBinding(
        Func<AppSettings, List<string>> Items,
        Action<AppSettings, List<string>> SetItems,
        Func<AppSettings, List<string>> Disabled,
        Action<AppSettings, List<string>> SetDisabled);

    /// <summary>A watched list with per-item on/off checkboxes and Add / Remove buttons.</summary>
    internal sealed class WatchedListSection
    {
        private readonly SettingsForm form;
        private readonly ListBinding binding;
        private readonly Label title;
        private readonly Label hint;
        private readonly FlowLayoutPanel buttons = Ui.Row();

        public WatchedListSection(SettingsForm form, string name, string titleText, string hintText, ListBinding binding)
        {
            this.form = form;
            this.binding = binding;
            title = Ui.Title(titleText);
            hint = Ui.Hint(hintText);
            List = new CheckedListBox { Name = name, Width = 460, Height = 96, CheckOnClick = true, IntegralHeight = false, Margin = new Padding(20, 2, 0, 2) };
            AddButton = Ui.Button(Strings.Get("Add"), (_, _) => { });
            AddButton.Name = name + "Add";
            RemoveButton = Ui.Button(Strings.Get("Remove"), (_, _) => RemoveSelected());
            RemoveButton.Name = name + "Remove";
            buttons.Margin = new Padding(20, 0, 0, 0);
            buttons.Controls.AddRange([AddButton, RemoveButton]);

            List.ItemCheck += (_, e) =>
            {
                if (form.refreshing) { return; }
                var item = (string)List.Items[e.Index];
                var enabled = e.NewValue == CheckState.Checked;
                // ItemCheck fires before the check state changes; apply afterwards.
                form.BeginInvoke(() => SetEnabled(item, enabled));
            };
            form.refreshers.Add(Refresh);
        }

        public CheckedListBox List { get; }
        public Button AddButton { get; }
        public Button RemoveButton { get; }

        public void AddTo(Control parent) => parent.Controls.AddRange([title, hint, List, buttons]);

        public void Add(string value) =>
            form.Update(s => binding.SetItems(s, WatchedItems.WithItemAdded(binding.Items(s), value)));

        public void SetEnabled(string item, bool enabled) =>
            form.Update(s => binding.SetDisabled(s, WatchedItems.WithItemEnabled(binding.Disabled(s), item, enabled)));

        private void RemoveSelected()
        {
            if (List.SelectedItem is not string item) { return; }
            form.Update(s =>
            {
                binding.SetItems(s, WatchedItems.WithItemRemoved(binding.Items(s), item));
                binding.SetDisabled(s, WatchedItems.WithItemRemoved(binding.Disabled(s), item));
            });
        }

        private void Refresh()
        {
            var settings = form.store.Settings;
            var off = new HashSet<string>(binding.Disabled(settings), StringComparer.OrdinalIgnoreCase);
            List.BeginUpdate();
            List.Items.Clear();
            foreach (var item in binding.Items(settings)) { List.Items.Add(item, !off.Contains(item)); }
            List.EndUpdate();
        }
    }

    #endregion

    #region Advanced

    private static readonly (string Key, Func<AppSettings, bool> Get, Action<AppSettings, bool> Set)[] AdvancedToggles =
    [
        ("QuitOnTimerExpiration", s => s.QuitOnTimerExpiration, (s, v) => s.QuitOnTimerExpiration = v),
        ("AllowDisplaySleep", s => s.AllowDisplaySleep, (s, v) => s.AllowDisplaySleep = v),
        ("PreventDiskSleep", s => s.PreventDiskSleep, (s, v) => s.PreventDiskSleep = v),
        ("DriveAlive", s => s.DriveAlive, (s, v) => s.DriveAlive = v),
        ("MouseJiggler", s => s.MouseJiggler, (s, v) => s.MouseJiggler = v),
        ("HideCountdown", s => s.CountdownHidden, (s, v) => s.CountdownHidden = v),
        ("DeactivateOnUserSwitch", s => s.DeactivateOnUserSwitch, (s, v) => s.DeactivateOnUserSwitch = v),
        ("ActivateOnExternalDisplay", s => s.ActivateOnExternalDisplay, (s, v) => s.ActivateOnExternalDisplay = v),
        ("ActivateOnACPower", s => s.ActivateOnACPower, (s, v) => s.ActivateOnACPower = v),
        ("ActivateDuringSchedule", s => s.ScheduleEnabled, (s, v) => s.ScheduleEnabled = v),
        ("ActivateWhileDownloading", s => s.DownloadInProgressActivationEnabled, (s, v) => s.DownloadInProgressActivationEnabled = v),
        ("ActivateOnExternalAudio", s => s.ActivateOnExternalAudioOutput, (s, v) => s.ActivateOnExternalAudioOutput = v),
    ];

    private Control BuildAdvanced()
    {
        var column = Ui.Column();
        column.Controls.Add(Ui.Text(Strings.Get("AdvancedHint")));
        foreach (var (key, get, set) in AdvancedToggles) { column.Controls.Add(Bound(key, Strings.Get(key), get, set)); }

        var cpuRow = Ui.Row();
        var cpu = Bound("ActivateOnCpuLoad", Strings.Get("ActivateOnCpuLoad"), s => s.ActivateOnCpuLoad, (s, v) => s.ActivateOnCpuLoad = v);
        cpu.MaximumSize = new Size(380, 0);
        var threshold = new NumericUpDown { Name = "CpuLoadThreshold", Minimum = 1, Maximum = 99, Width = 60, Margin = new Padding(6, 2, 2, 0) };
        threshold.ValueChanged += (_, _) => Update(s => s.CpuLoadThreshold = (double)threshold.Value);
        cpuRow.Controls.AddRange([cpu, threshold, new Label { Text = "%", AutoSize = true, Margin = new Padding(0, 6, 0, 0) }]);
        column.Controls.Add(cpuRow);
        refreshers.Add(() => threshold.Value = (decimal)Math.Round(store.Settings.EffectiveCpuLoadThreshold));

        var reset = Ui.Button(Strings.Get("ResetToDefault"), (_, _) => store.Update(s => s.ResetAdvanced()));
        reset.Name = "ResetAdvanced";
        column.Controls.Add(reset);
        return column;
    }

    #endregion

    #region Updates

    private Control BuildUpdates()
    {
        var column = Ui.Column();
        column.Controls.Add(Bound("AutomaticallyCheckForUpdates", Strings.Get("AutomaticallyCheckForUpdates"),
            s => s.AutomaticallyCheckForUpdates, (s, v) => s.AutomaticallyCheckForUpdates = v));
        column.Controls.Add(Bound("PreReleaseUpdates", Strings.Get("PreReleaseUpdates"), s => s.PreReleaseUpdates, (s, v) => s.PreReleaseUpdates = v));
        var lastChecked = Ui.Text(string.Empty);
        lastChecked.Name = "LastChecked";
        Button? checkNow = null;
        checkNow = Named(Ui.Button(Strings.Get("CheckNow"), async (_, _) =>
        {
            checkNow!.Enabled = false;
            try { await CheckForUpdatesAsync(); }
            finally
            {
                if (!IsDisposed) { checkNow.Enabled = true; }
            }
        }), "CheckNow");
        column.Controls.Add(checkNow);
        column.Controls.Add(lastChecked);
        refreshers.Add(() => lastChecked.Text = store.Settings.LastUpdateCheck is { } last
            ? Strings.Format("LastCheckedFormat", last.LocalDateTime.ToString("g"))
            : Strings.Get("NeverChecked"));
        return column;
    }

    internal async Task CheckForUpdatesAsync()
    {
        try
        {
            var release = await updates.CheckNowAsync();
            if (release is null)
            {
                prompts.Inform(this, Strings.Format("UpToDateFormat", UpdateService.CurrentVersion), Strings.Get("AppName"));
                return;
            }
            var open = prompts.Confirm(this, Strings.Format("UpdateAvailableFormat", release.Tag, UpdateService.CurrentVersion),
                Strings.Get("UpdateAvailableTitle"));
            if (open) { prompts.OpenUrl(release.PageUrl); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            prompts.Inform(this, Strings.Format("UpdateCheckFailedFormat", ex.Message), Strings.Get("AppName"), MessageBoxIcon.Warning);
        }
    }

    #endregion

    #region About

    private Control BuildAbout()
    {
        var column = Ui.Column(new Padding(20));
        using (var icon = TrayIcons.AppIcon())
        {
            column.Controls.Add(new PictureBox
            {
                Image = new Icon(icon, 64, 64).ToBitmap(),
                SizeMode = PictureBoxSizeMode.AutoSize,
                Margin = new Padding(0, 0, 0, 8),
            });
        }
        var name = Ui.Title("KeepingYouAwake for Windows");
        name.Font = new Font(name.Font.FontFamily, name.Font.Size * 1.4f, FontStyle.Bold);
        column.Controls.Add(name);
        var version = Ui.Text(Strings.Format("VersionFormat", UpdateService.CurrentVersion));
        version.Name = "Version";
        column.Controls.Add(version);
        column.Controls.Add(Ui.Text(Strings.Get("AboutDescription")));
        column.Controls.Add(Ui.Text(Strings.Get("AboutCredits")));
        column.Controls.Add(Ui.Text("© 2014–2026 Marcel Dierkes · © 2026 ryouka0731"));
        var link = new LinkLabel { Name = "Website", Text = Strings.Get("OpenWebsite"), AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        link.LinkClicked += (_, _) => prompts.OpenUrl(UpdateChecker.ProjectUrl);
        column.Controls.Add(link);
        return column;
    }

    #endregion
}
