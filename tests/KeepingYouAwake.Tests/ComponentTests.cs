using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using KeepingYouAwake.Core.Tests;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.Tests;

/// <summary>The tray context: icon state, tooltip, menu rendering and command handling.</summary>
[Collection(StringsCollection.Name)]
public class TrayApplicationContextTests
{
    [Fact]
    public void ClickingMenuItemsDrivesTheController() => Sta.Run(() =>
    {
        Strings.OverrideLanguage("en");
        try
        {
            using var tray = new TrayHarness(s => s.DefaultDurationSeconds = 300);
            Assert.Equal("KeepingYouAwake — inactive", tray.Context.TooltipText);

            tray.Context.RebuildMenu();
            var first = (ToolStripMenuItem)tray.Context.Menu.Items[0];
            Assert.Equal("Activate (5 minutes)", first.Text);
            first.PerformClick();
            Assert.True(tray.Controller.IsActive);
            Assert.StartsWith("KeepingYouAwake — 5:00", tray.Context.TooltipText);
            Assert.Equal("Preventing Sleep for 5 minutes", tray.Context.LastNotification?.Title);

            tray.Context.RebuildMenu();
            var deactivate = tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text == "Deactivate");
            deactivate.PerformClick();
            Assert.False(tray.Controller.IsActive);
            Assert.Equal("Allowing Sleep", tray.Context.LastNotification?.Title);
        }
        finally { Strings.OverrideLanguage(null); }
    });

    [Fact]
    public void DurationSubmenusActivateAndSetTheDefault() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        tray.Context.RebuildMenu();
        var submenus = tray.Context.Menu.Items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems).ToList();
        Assert.Equal(2, submenus.Count);

        var tenMinutes = submenus[0].DropDownItems.OfType<ToolStripMenuItem>()
            .Single(i => ((TrayMenuItem)i.Tag!).Duration == ActivationDuration.FromMinutes(10));
        tenMinutes.PerformClick();
        Assert.Equal(ActivationDuration.FromMinutes(10), tray.Controller.CurrentDuration);

        var untilMidnight = submenus[0].DropDownItems.OfType<ToolStripMenuItem>()
            .Single(i => ((TrayMenuItem)i.Tag!).Action == TrayMenuAction.ActivateUntilEndOfDay);
        untilMidnight.PerformClick();
        Assert.InRange(tray.Controller.CurrentDuration!.Value.Seconds, 1, 86400);

        var defaultTwoHours = submenus[1].DropDownItems.OfType<ToolStripMenuItem>()
            .Single(i => ((TrayMenuItem)i.Tag!).Duration == ActivationDuration.FromMinutes(120));
        defaultTwoHours.PerformClick();
        Assert.Equal(ActivationDuration.FromMinutes(120), tray.Controller.Durations.Default);

        tray.Context.RebuildMenu(); // old items are disposed, not leaked
        Assert.True(tenMinutes.IsDisposed);
    });

    [Fact]
    public void CommandLineArgumentsAreHandled() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        tray.Context.Handle(["--activate", "90"]);
        Assert.Equal(new ActivationDuration(90), tray.Controller.CurrentDuration);
        tray.Context.Handle(["keepingyouawake:///toggle"]);
        Assert.False(tray.Controller.IsActive);
        tray.Context.Handle(["--unknown"]);
        tray.Context.Handle([]);
        Assert.False(tray.Controller.IsActive);

        var settings = tray.Context.ShowSettings();
        Assert.Same(settings, tray.Context.ShowSettings()); // reused while open
        var log = tray.Context.ShowActivityLog();
        Assert.True(log.Visible);

        tray.Context.Handle(["--quit"]);
        Assert.True(tray.Context.IsExiting);
        Assert.True(settings.IsDisposed || !settings.Visible);
    });

    [Fact]
    public void QuitOnExpirationExitsTheContext() => Sta.Run(() =>
    {
        using var tray = new TrayHarness(s => s.QuitOnTimerExpiration = true);
        tray.Controller.Activate(TimeSpan.FromSeconds(30));
        tray.Fakes.Scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.True(tray.Context.IsExiting);
        Assert.False(tray.Fakes.Assertion.Held);
    });

    [Fact]
    public void SettingsChangesUpdateTheTooltip() => Sta.Run(() =>
    {
        Strings.OverrideLanguage("en");
        try
        {
            using var tray = new TrayHarness();
            tray.Controller.Activate(TimeSpan.FromMinutes(2));
            Assert.Contains("left", tray.Context.TooltipText);
            tray.Fakes.Store.Update(s => s.CountdownHidden = true);
            Assert.Equal("KeepingYouAwake — preventing sleep", tray.Context.TooltipText);
        }
        finally { Strings.OverrideLanguage(null); }
    });

    [Fact]
    public void TrayIconsMatchTheSmallIconSize() => Sta.Run(() =>
    {
        using var icons = new TrayIcons();
        Assert.Equal(SystemInformation.SmallIconSize, icons.Active.Size);
        Assert.Equal(SystemInformation.SmallIconSize, icons.Inactive.Size);
        icons.Rebuild();
        icons.ReleaseStale();
        using var app = TrayIcons.AppIcon();
        Assert.True(app.Width > 0);
    });
}

/// <summary>The Settings window's controls read and write the settings store.</summary>
public class SettingsFormTests
{
    private static SettingsForm NewForm(TrayHarness tray)
    {
        var form = tray.Context.ShowSettings();
        Sta.Pump(50);
        return form;
    }

    [Fact]
    public void CheckboxesAreBoundBothWays() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);

        var jiggler = form.Find<CheckBox>("MouseJiggler");
        Assert.False(jiggler.Checked);
        jiggler.Checked = true;
        Assert.True(tray.Fakes.Store.Settings.MouseJiggler);

        tray.Fakes.Store.Update(s => s.ShowNotifications = false);
        Assert.False(form.Find<CheckBox>("ShowNotifications").Checked);

        Sta.Click(form.Find<Button>("ResetAdvanced"));
        Assert.False(jiggler.Checked);
        Assert.False(tray.Fakes.Store.Settings.MouseJiggler);
    });

    [Fact]
    public void NumericAndSliderSettings() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);

        form.Find<NumericUpDown>("CpuLoadThreshold").Value = 75;
        Assert.Equal(75, tray.Fakes.Store.Settings.CpuLoadThreshold);

        form.Find<CheckBox>("BatteryCapacityThresholdEnabled").Checked = true;
        var slider = form.Find<TrackBar>("BatteryThreshold");
        Assert.True(slider.Enabled);
        slider.Value = 40;
        Assert.Equal(40, tray.Fakes.Store.Settings.BatteryCapacityThreshold);
        Assert.False(string.IsNullOrEmpty(form.Find<Label>("BatteryStatus").Text));
    });

    [Fact]
    public void DurationListButtons() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);
        var list = form.Find<ListBox>("Durations");
        var remove = form.Find<Button>("RemoveDuration");
        var setDefault = form.Find<Button>("SetDefaultDuration");
        Assert.Equal(tray.Controller.Durations.Durations.Count, list.Items.Count);

        list.SelectedIndex = 0; // Indefinitely: can't be removed, already the default
        Assert.False(remove.Enabled);
        Assert.False(setDefault.Enabled);

        list.SelectedIndex = 1;
        Sta.Click(setDefault);
        Assert.Equal(tray.Controller.Durations.Durations[1], tray.Controller.Durations.Default);
        Assert.Contains("(", (string)list.Items[1]);

        var count = list.Items.Count;
        list.SelectedIndex = 2;
        Sta.Click(remove);
        Assert.Equal(count - 1, list.Items.Count);
    });

    [Fact]
    public void WatchedListsAddToggleAndRemove() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);

        var apps = form.ApplicationsSection;
        apps.Add("obs64.exe");
        apps.Add("OBS64.EXE"); // duplicate
        Assert.Equal(["obs64.exe"], tray.Fakes.Store.Settings.WatchedApplications);
        Assert.Single(apps.List.Items);

        apps.List.SetItemChecked(0, false); // ItemCheck applies after the message loop runs
        Sta.PumpUntil(() => tray.Fakes.Store.Settings.DisabledWatchedApplications.Count == 1);
        apps.SetEnabled("obs64.exe", true);
        Assert.Empty(tray.Fakes.Store.Settings.DisabledWatchedApplications);

        apps.List.SelectedIndex = 0;
        Sta.Click(apps.RemoveButton);
        Assert.Empty(tray.Fakes.Store.Settings.WatchedApplications);

        var downloads = form.DownloadsSection;
        downloads.Add(@"D:\Downloads");
        Assert.Equal([@"D:\Downloads"], tray.Fakes.Store.Settings.DownloadDirectories!);
        downloads.List.SelectedIndex = 0;
        Sta.Click(downloads.RemoveButton);
        Assert.Null(tray.Fakes.Store.Settings.DownloadDirectories); // back to the Downloads folder
    });

    [Fact]
    public void WiFiAddMenuReflectsTheReading() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);
        var wifi = form.WiFiSection;

        var connected = form.WiFiAddMenuItems(new WiFiReading(WiFiReadStatus.Connected, ["Office", "Lab"]), wifi).ToList();
        Assert.Equal(4, connected.Count); // two networks, separator, manual entry
        connected[1].PerformClick();
        Assert.Equal(["Lab"], tray.Fakes.Store.Settings.WatchedWiFiSsids);

        var denied = form.WiFiAddMenuItems(new WiFiReading(WiFiReadStatus.AccessDenied, []), wifi).ToList();
        Assert.False(denied[0].Enabled);
        Assert.Equal(4, denied.Count);

        var offline = form.WiFiAddMenuItems(new WiFiReading(WiFiReadStatus.NotConnected, []), wifi).ToList();
        Assert.False(offline[0].Enabled);
        Assert.Equal(3, offline.Count);
    });

    [Fact]
    public void ScheduleWindowsAreListedAndToggled() => Sta.Run(() =>
    {
        using var tray = new TrayHarness(s => s.ScheduleWindows =
            [new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 540, EndMinutes = 1020 }]);
        var form = NewForm(tray);
        var list = form.Find<CheckedListBox>("ActiveHours");
        Assert.Single(list.Items);
        Assert.True(list.GetItemChecked(0));

        list.SetItemChecked(0, false);
        Sta.PumpUntil(() => !tray.Fakes.Store.Settings.ScheduleWindows[0].Enabled);
    });

    [Fact]
    public void StartAtLoginWritesTheRegistrySandbox() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);
        var box = form.Find<CheckBox>("StartAtLogin");
        Assert.False(box.Checked);
        box.Checked = true;
        Assert.True(box.Checked);
        box.Checked = false;
        Assert.False(box.Checked);
    });

    [Fact]
    public void AboutAndUpdatesTabs() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = NewForm(tray);
        Assert.Equal(7, form.Tabs.TabPages.Count);
        Assert.Contains(UpdateService.CurrentVersion, form.Find<Label>("Version").Text);
        Assert.False(string.IsNullOrEmpty(form.Find<Label>("LastChecked").Text));
        tray.Fakes.Store.Update(s => s.LastUpdateCheck = DateTimeOffset.Now);
        Assert.NotEqual(Strings.Get("NeverChecked"), form.Find<Label>("LastChecked").Text);
    });
}

/// <summary>Dialogs and the activity log window.</summary>
public class DialogTests
{
    [Fact]
    public void AddDurationDialogValidates() => Sta.Run(() =>
    {
        using var dialog = new AddDurationDialog();
        Assert.Equal(new ActivationDuration(45 * 60), dialog.Duration); // default 45 minutes
        var fields = AllControls(dialog).OfType<NumericUpDown>().ToList();
        Assert.Equal(3, fields.Count);
        fields.ForEach(f => f.Value = 0);
        Assert.Null(dialog.Duration);
        fields[0].Value = 2;
        Assert.Equal(new ActivationDuration(7200), dialog.Duration);
    });

    [Fact]
    public void ScheduleWindowDialogRoundTrips() => Sta.Run(() =>
    {
        var window = new ScheduleWindow { Weekdays = [DayOfWeek.Saturday, DayOfWeek.Sunday], StartMinutes = 22 * 60, EndMinutes = 6 * 60, Enabled = false };
        using var dialog = new ScheduleWindowDialog(window);
        var result = dialog.Window;
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Saturday], result.Weekdays);
        Assert.Equal(22 * 60, result.StartMinutes);
        Assert.Equal(6 * 60, result.EndMinutes);
        Assert.False(result.Enabled);

        using var fresh = new ScheduleWindowDialog(null);
        Assert.Equal(5, fresh.Window.Weekdays.Count); // weekdays by default
    });

    [Fact]
    public void TextPromptAndRunningApps() => Sta.Run(() =>
    {
        using var prompt = new TextPromptDialog("Title", "Label");
        AllControls(prompt).OfType<TextBox>().Single().Text = "Office";
        Assert.Equal("Office", prompt.Value);

        using var running = new RunningAppDialog();
        var list = AllControls(running).OfType<ListBox>().Single();
        Assert.True(list.Items.Count > 0);
        Assert.Null(running.SelectedExecutable);
        list.SelectedIndex = 0;
        Assert.EndsWith(".exe", running.SelectedExecutable);
    });

    [Fact]
    public void ActivityLogFormListsEntries() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        tray.Controller.Activate(TimeSpan.FromMinutes(5));
        tray.Controller.Deactivate();
        tray.Controller.Activate(null);
        var form = tray.Context.ShowActivityLog();
        Sta.Pump(100);
        var view = AllControls(form).OfType<ListView>().Single();
        Assert.Equal(2, view.Items.Count);
        Assert.Equal(Strings.Get("StillActive"), view.Items[0].SubItems[1].Text);
        Assert.Equal(Strings.Get("Indefinitely"), view.Items[0].SubItems[3].Text);
        Assert.Equal(Strings.ReasonName("user-cancelled"), view.Items[1].SubItems[4].Text);
    });

    internal static IEnumerable<Control> AllControls(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => AllControls(c).Prepend(c));
}
