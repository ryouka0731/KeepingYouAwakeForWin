using System.Text;
using System.Text.Json.Nodes;
using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core.Tests;

/// <summary>
/// Tests written from Stryker.NET's surviving mutants: each pins down a boundary, a
/// disabled path or a side effect that the behaviour tests exercised but didn't assert.
/// </summary>
public class ControllerMutationTests
{
    [Fact]
    public void BatteryThresholdIsInclusive()
    {
        using var h = new ControllerHarness(s =>
        {
            s.BatteryCapacityThresholdEnabled = true;
            s.BatteryCapacityThreshold = 20;
        });
        h.Power.Set(BatteryState.Unplugged, 50);
        h.Controller.Start();
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Unplugged, 20);
        Assert.False(h.Controller.IsActive);

        // Started exactly at the threshold: the user's override applies.
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Unplugged, 19);
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void BatteryRulesOnlyApplyWhenEnabledAndOnBattery()
    {
        using var h = new ControllerHarness(s =>
        {
            s.BatteryCapacityThresholdEnabled = true;
            s.BatteryCapacityThreshold = 30;
        });
        h.Power.Set(BatteryState.Charging, 80);
        h.Controller.Start();
        h.Controller.Activate(null);

        h.Power.Set(BatteryState.Charging, 5);               // low, but plugged in
        h.Power.Set(BatteryState.Full, 100);                 // full-charge rule is off
        h.Power.Set(BatteryState.Unplugged, 90, saver: true); // battery-saver rule is off
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void StateChangesAndLogFieldsAreReported()
    {
        using var h = new ControllerHarness();
        var changes = 0;
        h.Controller.StateChanged += (_, _) => changes++;
        h.Controller.Start();

        h.Controller.Activate(null);
        Assert.Equal(1, changes);
        Assert.Equal(-1, h.Log.RecentEntries(1)[0].RequestedDuration);
        h.Controller.Deactivate();
        Assert.Equal(2, changes);
        Assert.Null(h.Controller.CurrentDuration);
        Assert.Null(h.Controller.CurrentSource);

        h.Controller.Activate(TimeSpan.FromSeconds(ActivationDuration.MaximumSeconds)); // the longest allowed
        Assert.True(h.Controller.IsActive);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void LeavingTheWatchedNetworkEndsItsSession()
    {
        using var h = new ControllerHarness(s => s.WatchedWiFiSsids = ["Office"]);
        h.WiFi.Ssids.Add("Office");
        h.Controller.Start();
        Assert.Equal(ActivationSource.WatchedSsid, h.Controller.CurrentSource);
        h.WiFi.Ssids.Clear();
        h.Controller.NetworkChanged();
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ShutdownIsSilentAndStopsEverything()
    {
        using var h = new ControllerHarness(s =>
        {
            s.QuitOnTimerExpiration = true;
            s.MouseJiggler = true;
            s.WatchedWiFiSsids = ["Office"];
            s.WatchedApplications = ["a.exe"];
            s.ActivateOnExternalAudioOutput = true;
            s.ActivateOnCpuLoad = true;
            s.ScheduleEnabled = true;
            s.ScheduleWindows = [new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 0, EndMinutes = 1 }];
            s.DownloadInProgressActivationEnabled = true;
        });
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        var notifications = h.Notifications.Count;
        var nudges = h.Nudger.Count;

        h.Controller.Shutdown();

        Assert.Equal(notifications, h.Notifications.Count); // no "allowing sleep" on quit
        Assert.Equal(0, h.QuitRequests);
        Assert.Null(h.Controller.CurrentSource);
        Assert.Equal(0, h.Scheduler.ActiveTimers);           // every monitor, the jiggler and the session timer stopped
        h.Scheduler.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(nudges, h.Nudger.Count);
    }

    [Fact]
    public void JigglerIsKeptAcrossUnrelatedSettingsChanges()
    {
        using var h = new ControllerHarness(s => s.MouseJiggler = true);
        h.Controller.Start();
        h.Controller.Activate(null);
        Assert.Equal(1, h.Nudger.Count);
        h.Store.Update(s => s.ShowNotifications = false);
        Assert.Equal(1, h.Nudger.Count); // not restarted (which would nudge again)
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) { return true; }
            Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void DiskKeepAliveSwitchesBetweenSystemOnlyAndAllDrives()
    {
        using var h = new ControllerHarness(s => s.PreventDiskSleep = true);
        var external = Directory.CreateDirectory(Path.Join(h.Directory, "external")).FullName;
        var ping = Path.Join(external, DriveAliveTimer.PingFileName);
        h.Drives.Roots.Add(external);
        h.Controller.Start();
        h.Controller.Activate(null);
        Thread.Sleep(200);
        Assert.False(File.Exists(ping)); // system drive only

        h.Store.Update(s => s.DriveAlive = true);
        Assert.True(WaitFor(() => File.Exists(ping)));

        h.Store.Update(s => s.DriveAlive = false); // back to the system drive only
        Assert.True(WaitFor(() => !File.Exists(ping)));

        h.Store.Update(s => s.DriveAlive = true);
        Assert.True(WaitFor(() => File.Exists(ping)));
        h.Store.Update(s => { s.DriveAlive = false; s.PreventDiskSleep = false; });
        Assert.True(WaitFor(() => !File.Exists(ping)));
    }

    [Fact]
    public void ACTriggerDisabledIgnoresPowerAndReengagesFresh()
    {
        using var h = new ControllerHarness();
        h.Power.Set(BatteryState.Unplugged);
        h.Controller.Start();
        h.Power.Set(BatteryState.Charging);
        Assert.False(h.Controller.IsActive); // trigger off

        h.Store.Update(s => s.ActivateOnACPower = true);
        Assert.Equal(ActivationSource.ACPower, h.Controller.CurrentSource);
        h.Store.Update(s => s.ShowNotifications = false); // unrelated change keeps it
        h.Power.Set(BatteryState.Full, 100);              // still on AC
        Assert.Equal(ActivationSource.ACPower, h.Controller.CurrentSource);

        h.Store.Update(s => s.ActivateOnACPower = false);
        Assert.False(h.Controller.IsActive);
        h.Store.Update(s => s.ActivateOnACPower = true); // first observation counts as plugging in again
        Assert.Equal(ActivationSource.ACPower, h.Controller.CurrentSource);
    }

    [Fact]
    public void DisplaySessionSurvivesUnrelatedSettingsChanges()
    {
        using var h = new ControllerHarness(s => s.ActivateOnExternalDisplay = true);
        h.Displays.Set(1);
        h.Controller.Start();
        h.Store.Update(s => s.ShowNotifications = false);
        Assert.Equal(ActivationSource.ExternalDisplay, h.Controller.CurrentSource);
    }

    [Fact]
    public void TurningATriggerOffEndsItsSessionAndStopsPolling()
    {
        var downloads = Path.Join(Path.GetTempPath(), "kya-mk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Join(downloads, "x.part"), "x");
        try
        {
            (Action<AppSettings> Enable, Action<AppSettings> Disable, Action<ControllerHarness> Arrange, ActivationSource Source)[] triggers =
            [
                (s => { s.ScheduleEnabled = true; s.ScheduleWindows = [AllWeek()]; }, s => s.ScheduleEnabled = false, _ => { }, ActivationSource.Schedule),
                (s => { s.DownloadInProgressActivationEnabled = true; s.DownloadDirectories = [downloads]; },
                    s => s.DownloadInProgressActivationEnabled = false, _ => { }, ActivationSource.Download),
                (s => s.ActivateOnExternalAudioOutput = true, s => s.ActivateOnExternalAudioOutput = false,
                    h => h.Audio.External = true, ActivationSource.AudioOutput),
                (s => s.ActivateOnCpuLoad = true, s => s.ActivateOnCpuLoad = false, h => h.Cpu.BusyPercent = 99, ActivationSource.CpuLoad),
            ];
            foreach (var (enable, disable, arrange, source) in triggers)
            {
                using var h = new ControllerHarness();
                arrange(h);
                h.Controller.Start();
                h.Store.Update(enable);
                h.Scheduler.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(source, h.Controller.CurrentSource);

                h.Store.Update(disable);
                Assert.False(h.Controller.IsActive);
                Assert.Equal(0, h.Scheduler.ActiveTimers); // the monitor stopped polling
                h.Scheduler.Advance(TimeSpan.FromMinutes(2));
                Assert.False(h.Controller.IsActive);
            }
        }
        finally
        {
            Directory.Delete(downloads, recursive: true);
        }
    }

    [Fact]
    public void EmptyingAWatchedListStopsItsMonitor()
    {
        using var h = new ControllerHarness(s => s.WatchedWiFiSsids = ["Office"]);
        h.Controller.Start();
        Assert.Equal(1, h.Scheduler.ActiveTimers);
        h.Store.Update(s => s.WatchedWiFiSsids = []);
        Assert.Equal(0, h.Scheduler.ActiveTimers);
    }

    private static ScheduleWindow AllWeek() =>
        new() { Weekdays = Enum.GetValues<DayOfWeek>().ToList(), StartMinutes = 0, EndMinutes = 0 };
}

[Collection(StringsCollection.Name)]
public class ModelMutationTests
{
    [Fact]
    public void DurationBoundariesAndDefaults()
    {
        Assert.Null(ActivationDuration.Indefinite.TimeSpan);
        Assert.Equal(TimeSpan.FromMinutes(5), ActivationDuration.FromMinutes(5).TimeSpan);
        Assert.Equal(59 * 60, ActivationDuration.FromComponents(0, 59, 0)!.Value.Seconds);
        var maxHours = (int)(ActivationDuration.MaximumSeconds / 3600);
        Assert.Equal(ActivationDuration.MaximumSeconds, ActivationDuration.FromComponents(maxHours, 0, 0)!.Value.Seconds);
        Assert.Null(ActivationDuration.FromComponents(maxHours, 0, 1));

        var store = new SettingsStore(null);
        var controller = new ActivationDurationsController(store);
        store.Update(s => s.DefaultDurationSeconds = 999); // not in the list
        Assert.Equal(ActivationDuration.Indefinite, controller.Default);
        controller.Default = new ActivationDuration(999); // can't pick a duration that isn't listed
        Assert.Equal(999, store.Settings.DefaultDurationSeconds);
        Assert.Equal(ActivationDuration.Indefinite, controller.Default);

        controller.Default = ActivationDuration.FromMinutes(10);
        Assert.True(controller.Remove(ActivationDuration.FromMinutes(5)));
        Assert.Equal(ActivationDuration.FromMinutes(10), controller.Default);  // removing another keeps the default
        Assert.Contains(ActivationDuration.FromMinutes(15), controller.Durations); // and the rest of the list
        Assert.DoesNotContain(ActivationDuration.FromMinutes(5), controller.Durations);
        Assert.False(controller.Remove(ActivationDuration.FromMinutes(5)));

        controller.Reset();
        Assert.Equal(ActivationDuration.FromMinutes(10), controller.Default); // still a default duration

        store.Update(s => s.Durations = [ActivationDuration.MaximumSeconds]);
        Assert.Contains(new ActivationDuration(ActivationDuration.MaximumSeconds), controller.Durations);
    }

    [Fact]
    public void SettingsSanitizeBoundariesAndNulls()
    {
        Assert.True(new AppSettings().AutomaticallyCheckForUpdates);
        var dir = Path.Join(Path.GetTempPath(), "kya-mk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Join(dir, "settings.json");
        try
        {
            File.WriteAllText(path, $$"""
            {
              "watchedApplications": null,
              "durations": [0, -5, {{ActivationDuration.MaximumSeconds}}, {{ActivationDuration.MaximumSeconds + 1}}],
              "defaultDurationSeconds": {{ActivationDuration.MaximumSeconds}},
              "scheduleWindows": null,
              "cpuLoadThreshold": "75",
              "batteryCapacityThreshold": "NaN",
            }
            """);
            var store = new SettingsStore(path);
            Assert.Empty(store.Settings.WatchedApplications);
            Assert.Empty(store.Settings.ScheduleWindows);
            Assert.Equal([ActivationDuration.MaximumSeconds], store.Settings.Durations);
            Assert.Equal(ActivationDuration.MaximumSeconds, store.Settings.DefaultDurationSeconds);
            Assert.Equal(75, store.Settings.CpuLoadThreshold);

            store.Update(s =>
            {
                s.DefaultDurationSeconds = -1;
                s.WatchedApplications = [" ", "a.exe"];
            });
            Assert.Equal(0, store.Settings.DefaultDurationSeconds);
            Assert.Equal(["a.exe"], store.Settings.WatchedApplications); // sanitized in memory too
            Assert.Contains("\n  \"", File.ReadAllText(path));       // written indented

            File.WriteAllText(path, """{ "durations": [60, 0], "defaultDurationSeconds": 99999999999 }""");
            Assert.True(store.ReloadFromDisk());
            Assert.Equal([60L], store.Settings.Durations);
            Assert.Equal(0, store.Settings.DefaultDurationSeconds);

            File.Delete(path);
            Assert.False(store.ReloadFromDisk());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ActivityLogKeepsClosedEntriesAndExactCap()
    {
        var dir = Path.Join(Path.GetTempPath(), "kya-mk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Join(dir, "activity.jsonl");
        try
        {
            File.WriteAllLines(path,
            [
                """{"startedAt":"2026-10-20T10:00:00Z","endedAt":"2026-10-20T10:05:00Z","source":"schedule","requestedDuration":-1,"endedReason":"trigger-cancelled"}""",
                """{"startedAt":"2026-10-20T11:00:00Z","endedAt":"2026-10-20T11:05:00Z","source":"user","requestedDuration":300,"endedReason":"expired"}""",
                """{"startedAt":"2026-10-20T12:00:00","source":"download","requestedDuration":-1}""",
            ]);
            var now = new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);
            var log = new ActivityLogger(path, maximumEntries: 4, clock: () => now);

            log.RecordEnded(ActivityLogEndedReasons.UserCancelled); // nothing open in this process: no-op
            log.CloseDanglingEntries(string.Empty);
            log.RecordEnded(ActivityLogEndedReasons.UserCancelled); // still nothing open
            var entries = log.RecentEntries(10);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 11, 5, 0, TimeSpan.Zero), entries[1].EndedAt); // closed entries untouched
            Assert.Equal("schedule", entries[2].Source);
            Assert.Equal(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero), entries[0].StartedAt); // no offset = UTC
            Assert.Equal(now, entries[0].EndedAt);
            Assert.Null(entries[0].EndedReason); // empty reason isn't written

            log.RecordStarted("user", 60);
            log.RecordEnded(ActivityLogEndedReasons.Expired);
            log.RecordEnded(ActivityLogEndedReasons.UserCancelled); // already closed
            Assert.Equal(4, log.RecentEntries(10).Count); // exactly at the cap: nothing dropped
            Assert.Equal(ActivityLogEndedReasons.Expired, log.RecentEntries(1)[0].EndedReason);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]); // UTF-8 without BOM
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("/activate", AppCommandKind.Activate)]
    [InlineData("--deactivate", AppCommandKind.Deactivate)]
    [InlineData("/deactivate", AppCommandKind.Deactivate)]
    [InlineData("--toggle", AppCommandKind.Toggle)]
    [InlineData("--settings", AppCommandKind.ShowSettings)]
    [InlineData("/quit", AppCommandKind.Quit)]
    [InlineData("KEEPINGYOUAWAKE:///toggle", AppCommandKind.Toggle)]
    public void EverySwitchSpelling(string argument, AppCommandKind kind) =>
        Assert.Equal(kind, AppCommand.ParseArguments([argument])!.Kind);

    [Fact]
    public void UrlEdgeCases()
    {
        Assert.Null(AppCommand.ParseUrl("https://activate")); // right action, wrong scheme
        Assert.Equal(TimeSpan.FromSeconds(ActivationDuration.MaximumSeconds),
            AppCommand.ParseUrl($"keepingyouawake:///activate?seconds={ActivationDuration.MaximumSeconds}")!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(5), AppCommand.ParseUrl("keepingyouawake:///activate?seconds=5&seconds=10")!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(4), AppCommand.ParseUrl("keepingyouawake:///activate?=3&seconds=4")!.Duration);
        Assert.Equal(TimeSpan.FromMinutes(2), AppCommand.ParseUrl("keepingyouawake:///activate?minutes=1%2E5")!.Duration);
        Assert.Equal(TimeSpan.FromSeconds(30), AppCommand.ParseArguments(["/activate", "30"])!.Duration);
    }

    [Fact]
    public void MonitorStartStopAndEvaluationEdges()
    {
        var scheduler = new FakeScheduler();
        var monitor = new PollingConditionMonitor(scheduler, TimeSpan.FromSeconds(1), () => true);
        monitor.Start();
        monitor.Start();
        Assert.Equal(1, scheduler.ActiveTimers);
        Assert.True(monitor.State);
        monitor.Stop();
        Assert.False(monitor.State);

        var dir = Path.Join(Path.GetTempPath(), "kya-mk-" + Guid.NewGuid().ToString("N"));
        var empty = Path.Join(dir, "empty");
        var busy = Path.Join(dir, "busy");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Join(busy, "a.crdownload"), "x");
        try
        {
            using var downloads = new DownloadActivityMonitor(new FakeScheduler());
            downloads.Start();
            downloads.SetDirectories([empty, busy]); // re-evaluated right away
            Assert.True(downloads.State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CpuDwellIsExactAndRestartsCleanly()
    {
        var scheduler = new FakeScheduler();
        var sampler = new FakeCpuSampler { BusyPercent = 90 };
        var monitor = new CpuLoadMonitor(scheduler, sampler) { ThresholdPercent = 50 };
        var events = new List<string>();
        monitor.CrossedAbove += (_, _) => events.Add("above");
        monitor.FellBelow += (_, _) => events.Add("below");

        monitor.Start();
        monitor.Start(); // idempotent: one sampling timer
        Assert.Equal(1, scheduler.ActiveTimers);
        scheduler.Advance(TimeSpan.FromSeconds(30)); // the first sample at start is the baseline
        Assert.Equal(["above"], events);

        sampler.BusyPercent = 10;
        scheduler.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(["above", "below"], events);
        Assert.False(monitor.IsAboveThreshold);

        monitor.Stop();
        Assert.Equal(0, scheduler.ActiveTimers);
        sampler.BusyPercent = 90;
        monitor.Start(); // dwell counters start over
        scheduler.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void ScheduleClampingOrderingAndFullDayWindows()
    {
        var monday = new DateTime(2026, 10, 19);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday],
            new ScheduleWindow { Weekdays = [DayOfWeek.Friday, DayOfWeek.Monday] }.Normalized().Weekdays);
        Assert.Equal(1439, new ScheduleWindow { EndMinutes = 5000 }.Normalized().EndMinutes);

        // Out-of-range minutes clamp to 23:59, the last minute of the day.
        var late = new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 1500, EndMinutes = 3000 };
        Assert.True(late.Contains(monday.AddMinutes(1439)));

        // Start == end: the window wraps into a full day.
        var allDay = new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 600, EndMinutes = 600 };
        Assert.True(allDay.Contains(monday.AddHours(10)));
        Assert.True(allDay.Contains(monday.AddDays(1).AddHours(7)));

        var scheduler = new FakeScheduler();
        using var schedule = new ScheduleMonitor(scheduler, _ => monday.AddHours(12));
        var entered = 0;
        schedule.Entered += (_, _) => entered++;
        schedule.SetWindows([allDay]);
        schedule.Start();
        scheduler.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, entered); // only on the transition
    }

    [Fact]
    public void PresenterTitlesAndFlags()
    {
        try
        {
            Strings.OverrideLanguage("en");
            var state = new TrayState(true, null, ActivationSource.Download, ActivationDuration.Indefinite,
                [ActivationDuration.Indefinite], ActivationDuration.Indefinite, false);
            var menu = TrayPresenter.BuildMenu(state, DateTimeOffset.UnixEpoch);
            Assert.False(menu[0].Enabled); // "Started by …" is a label
            Assert.Equal(Strings.Get("Deactivate"), menu[1].Title);
            Assert.Equal([Strings.Get("SettingsEllipsis"), Strings.Get("ShowActivityLogEllipsis"), Strings.Get("Quit")],
                menu.Where(i => i.Action is TrayMenuAction.ShowSettings or TrayMenuAction.ShowActivityLog or TrayMenuAction.Quit).Select(i => i.Title));
            Assert.Equal(Strings.Get("AllowingSleepBody"), TrayPresenter.Notification(new SessionNotification(false, null), DateTimeOffset.UnixEpoch).Body);
        }
        finally
        {
            Strings.OverrideLanguage(null);
        }
    }

    [Fact]
    public void UpdateSelectionEdges()
    {
        const string releases = """[{"tag_name":"v5.0.0","prerelease":true},{"tag_name":"v4.0.0"}]""";
        Assert.Null(UpdateChecker.SelectUpdate(releases, "dev-build", includePreReleases: true));
        Assert.Equal("v4.0.0", UpdateChecker.SelectUpdate(releases, "1.0.0", includePreReleases: false)!.Tag);
        Assert.Equal("v5.0.0", UpdateChecker.SelectUpdate(releases, "1.0.0", includePreReleases: true)!.Tag);
    }

    [Fact]
    public void WatchedItemAndDecodingEdges()
    {
        Assert.Equal("Downloads", Path.GetFileName(WatchedItems.DefaultDownloadsFolder));
        Assert.Equal(["A"], WatchedItems.Enabled(["A", "a"], ["a"], ignoreCase: false));
        Assert.Equal("obs.exe", WatchedItems.NormalizeExecutableName(@"\obs.exe"));
        Assert.Equal(string.Empty, WatchedItems.NormalizeExecutableName("  "));
        Assert.True(WatchedItems.AnyJoined(["Office", "Lab"], ["lab"]));

        Assert.Null(WindowsDecoding.ParseConnectionSsid(new byte[WindowsDecoding.WlanConnectionSsidOffset]));
        Assert.Equal(["a", "b"], InstanceMessage.Decode("  a  \n b"u8));
    }

    [Fact]
    public void DrivePingsCarryTheTimersToken()
    {
        var dir = Path.Join(Path.GetTempPath(), "kya-mk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var drives = new FakeDriveEnumerator();
            drives.Roots.Add(dir);
            using var timer = new DriveAliveTimer(new FakeScheduler(), drives, dir, runInline: true);
            timer.Start();
            var content = File.ReadAllText(Path.Join(dir, DriveAliveTimer.PingFileName), Encoding.UTF8);
            Assert.Matches("^keepingyouawake [0-9a-f]{32} ", content);
            Assert.False(DriveAliveTimer.WritePing(Path.Join(dir, "missing", "ping"), "keepingyouawake x"u8.ToArray(), hidden: false));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParsedLogSourceComesFromTheEntry() =>
        Assert.Equal("cpu-load", ActivityLogger.Parse(JsonNode.Parse(
            """{"startedAt":"2026-10-21T00:00:00Z","source":"cpu-load"}""")!.AsObject())!.Source);
}
