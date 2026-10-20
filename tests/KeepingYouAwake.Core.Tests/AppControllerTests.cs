using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core.Tests;

public class AppControllerTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public void ToggleStartsDefaultDurationAndStops()
    {
        using var h = new ControllerHarness(s => s.DefaultDurationSeconds = 300);
        h.Controller.Start();

        h.Controller.Toggle();
        Assert.True(h.Controller.IsActive);
        Assert.True(h.Assertion.Held);
        Assert.Equal(new ActivationDuration(300), h.Controller.CurrentDuration);
        Assert.Equal(ActivationSource.User, h.Controller.CurrentSource);

        h.Controller.Toggle();
        Assert.False(h.Controller.IsActive);
        Assert.False(h.Assertion.Held);
        Assert.Equal(ActivityLogEndedReasons.UserCancelled, h.Log.RecentEntries(1)[0].EndedReason);
    }

    [Fact]
    public void SessionExpiresAndLogsExpiry()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(5));

        h.Scheduler.Advance(TimeSpan.FromMinutes(5));

        Assert.False(h.Controller.IsActive);
        Assert.False(h.Assertion.Held);
        var entry = h.Log.RecentEntries(1)[0];
        Assert.Equal(ActivityLogEndedReasons.Expired, entry.EndedReason);
        Assert.Equal(300, entry.RequestedDuration);
        Assert.Equal(0, h.QuitRequests);
    }

    [Fact]
    public void QuitOnExpirationRequestsQuitOnlyOnExpiry()
    {
        using var h = new ControllerHarness(s => s.QuitOnTimerExpiration = true);
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Controller.Deactivate();
        Assert.Equal(0, h.QuitRequests);

        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Scheduler.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, h.QuitRequests);
    }

    [Fact]
    public void ReplacingASessionClosesTheOldLogEntryAndKeepsTheNewSession()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Controller.Activate(TimeSpan.FromMinutes(10));

        h.Scheduler.Advance(TimeSpan.FromMinutes(2));

        Assert.True(h.Controller.IsActive);
        var entries = h.Log.RecentEntries(10);
        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].EndedAt);
        Assert.Equal(ActivityLogEndedReasons.UserCancelled, entries[1].EndedReason);
    }

    [Fact]
    public void NotificationsFollowTheSetting()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Controller.Deactivate();
        Assert.Equal([true, false], h.Notifications.Select(n => n.Activating));

        h.Store.Update(s => s.ShowNotifications = false);
        h.Notifications.Clear();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Controller.Deactivate();
        Assert.Empty(h.Notifications);
    }

    [Fact]
    public void UrlActivateUsesDefaultDurationWithoutParameters()
    {
        using var h = new ControllerHarness(s => s.DefaultDurationSeconds = 600);
        h.Controller.Start();
        h.Controller.Handle(AppCommand.ParseUrl("keepingyouawake:///activate")!);
        Assert.Equal(new ActivationDuration(600), h.Controller.CurrentDuration);

        h.Controller.Handle(AppCommand.ParseUrl("keepingyouawake:///activate?seconds=0")!);
        Assert.Equal(ActivationDuration.Indefinite, h.Controller.CurrentDuration);
        Assert.Null(h.Controller.FireDate);
    }

    [Fact]
    public void ActivateOnLaunch()
    {
        using var h = new ControllerHarness(s => s.ActivateOnLaunch = true);
        h.Controller.Start();
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void StartClosesDanglingEntriesFromACrashedRun()
    {
        using var h = new ControllerHarness();
        h.Log.RecordStarted("user", -1); // as if the previous process died here
        var fresh = new ActivityLogger(h.Log.FilePath);
        Assert.Null(fresh.RecentEntries(1)[0].EndedAt);

        h.Controller.Start();

        Assert.Equal(ActivityLogEndedReasons.AppTerminated, h.Log.RecentEntries(1)[0].EndedReason);
    }

    [Fact]
    public void ShutdownClosesTheOpenEntry()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(null);
        h.Controller.Shutdown();

        Assert.False(h.Assertion.Held);
        Assert.Equal(ActivityLogEndedReasons.AppTerminated, h.Log.RecentEntries(1)[0].EndedReason);
    }

    // --- Watched applications ---

    [Fact]
    public void WatchedApplicationStartsAndEndsItsSession()
    {
        using var h = new ControllerHarness(s => s.WatchedApplications = ["obs64"]);
        h.Controller.Start();
        Assert.False(h.Controller.IsActive);

        h.Processes.Running.Add("OBS64.EXE");
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.True(h.Controller.IsActive);
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);

        h.Processes.Running.Clear();
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.False(h.Controller.IsActive);
        Assert.Equal(ActivityLogEndedReasons.TriggerCancelled, h.Log.RecentEntries(1)[0].EndedReason);
    }

    [Fact]
    public void TriggerNeverEndsAUserSession()
    {
        using var h = new ControllerHarness(s => s.WatchedApplications = ["obs64.exe"]);
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromHours(1));

        h.Processes.Running.Add("obs64.exe");
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        h.Processes.Running.Clear();
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));

        Assert.True(h.Controller.IsActive);
        Assert.Equal(ActivationSource.User, h.Controller.CurrentSource);
    }

    [Fact]
    public void TriggerDoesNotReplaceAnotherTriggersSession()
    {
        using var h = new ControllerHarness(s =>
        {
            s.WatchedApplications = ["a.exe"];
            s.WatchedWiFiSsids = ["Office"];
        });
        h.Controller.Start();
        h.Processes.Running.Add("a.exe");
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);

        h.WiFi.Ssids.Add("office");
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);

        // Leaving Wi-Fi must not end the watched-app session.
        h.WiFi.Ssids.Clear();
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void AddingARunningAppToTheListStartsASessionAndRemovingItEndsIt()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Processes.Running.Add("game.exe");

        h.Store.Update(s => s.WatchedApplications = ["game.exe"]);
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);

        h.Store.Update(s => s.DisabledWatchedApplications = ["GAME.exe"]);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void UnrelatedSettingsChangeDoesNotRestartATriggerSessionTheUserEnded()
    {
        using var h = new ControllerHarness(s => s.WatchedApplications = ["game.exe"]);
        h.Processes.Running.Add("game.exe");
        h.Controller.Start();
        Assert.True(h.Controller.IsActive);

        h.Controller.Deactivate();
        h.Store.Update(s => s.MouseJiggler = true);
        h.Scheduler.Advance(TimeSpan.FromSeconds(30));

        Assert.False(h.Controller.IsActive);
    }

    // --- Wi-Fi ---

    [Fact]
    public void WatchedWiFiIsCaseInsensitiveAndRespectsDisabledEntries()
    {
        using var h = new ControllerHarness(s =>
        {
            s.WatchedWiFiSsids = ["Home-5G", "Office"];
            s.DisabledWatchedWiFiSsids = ["office"];
        });
        h.Controller.Start();

        h.WiFi.Ssids.Add("OFFICE");
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.False(h.Controller.IsActive);

        h.WiFi.Ssids.Add("home-5g");
        h.Controller.NetworkChanged();
        Assert.Equal(ActivationSource.WatchedSsid, h.Controller.CurrentSource);
    }

    // --- AC power ---

    [Fact]
    public void ACPowerTriggerActsOnTransitionsOnly()
    {
        using var h = new ControllerHarness();
        h.Power.Set(BatteryState.Unplugged);
        h.Controller.Start();
        h.Store.Update(s => s.ActivateOnACPower = true);
        Assert.False(h.Controller.IsActive);

        h.Power.Set(BatteryState.Charging);
        Assert.Equal(ActivationSource.ACPower, h.Controller.CurrentSource);

        // Ended by the user while still on AC: further battery updates don't restart it.
        h.Controller.Deactivate();
        h.Power.Set(BatteryState.Charging, 81);
        Assert.False(h.Controller.IsActive);

        h.Power.Set(BatteryState.Unplugged);
        h.Power.Set(BatteryState.Full);
        Assert.True(h.Controller.IsActive);
        h.Power.Set(BatteryState.Unplugged);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ACPowerTriggerActivatesWhenEnabledWhilePluggedIn()
    {
        using var h = new ControllerHarness();
        h.Power.Set(BatteryState.Charging);
        h.Controller.Start();
        h.Store.Update(s => s.ActivateOnACPower = true);
        Assert.Equal(ActivationSource.ACPower, h.Controller.CurrentSource);

        h.Store.Update(s => s.ActivateOnACPower = false);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ACPowerTriggerIgnoresDesktops()
    {
        using var h = new ControllerHarness(s => s.ActivateOnACPower = true);
        h.Controller.Start();
        h.Power.Set(BatteryState.Unknown);
        Assert.False(h.Controller.IsActive);
    }

    // --- Battery rules ---

    [Fact]
    public void BatteryThresholdEndsSessionUnlessStartedBelowIt()
    {
        using var h = new ControllerHarness(s =>
        {
            s.BatteryCapacityThresholdEnabled = true;
            s.BatteryCapacityThreshold = 20;
        });
        h.Power.Set(BatteryState.Unplugged, 50);
        h.Controller.Start();
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Unplugged, 19);
        Assert.False(h.Controller.IsActive);

        // Started below the threshold: the user wants to stay awake anyway.
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Unplugged, 15);
        Assert.True(h.Controller.IsActive);
    }

    [Fact]
    public void FullChargeAndBatterySaverEndSessions()
    {
        using var h = new ControllerHarness(s =>
        {
            s.DeactivateOnFullCharge = true;
            s.BatterySaverMonitoringEnabled = true;
        });
        h.Power.Set(BatteryState.Charging, 90);
        h.Controller.Start();
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Full, 100);
        Assert.False(h.Controller.IsActive);

        h.Power.Set(BatteryState.Unplugged, 60);
        h.Controller.Activate(null);
        h.Power.Set(BatteryState.Unplugged, 59, saver: true);
        Assert.False(h.Controller.IsActive);
    }

    // --- External display ---

    [Fact]
    public void ExternalDisplayTrigger()
    {
        using var h = new ControllerHarness(s => s.ActivateOnExternalDisplay = true);
        h.Controller.Start();
        h.Displays.Set(1);
        Assert.Equal(ActivationSource.ExternalDisplay, h.Controller.CurrentSource);
        h.Displays.Set(0);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ExternalDisplayAlreadyConnectedAtLaunchActivates()
    {
        using var h = new ControllerHarness(s => s.ActivateOnExternalDisplay = true);
        h.Displays.Set(2);
        h.Controller.Start();
        Assert.Equal(ActivationSource.ExternalDisplay, h.Controller.CurrentSource);
    }

    // --- User switching ---

    [Fact]
    public void UserSwitchSuspendsAndResumesTheSession()
    {
        using var h = new ControllerHarness(s =>
        {
            s.DeactivateOnUserSwitch = true;
            s.WatchedApplications = ["a.exe"];
        });
        h.Processes.Running.Add("a.exe");
        h.Controller.Start();
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);

        h.Sessions.ResignActive();
        Assert.False(h.Controller.IsActive);
        h.Sessions.BecomeActive();
        Assert.Equal(ActivationSource.WatchedApp, h.Controller.CurrentSource);
    }

    [Fact]
    public void ReturningWithoutASuspendedSessionDoesNotActivate()
    {
        using var h = new ControllerHarness(s => s.DeactivateOnUserSwitch = true);
        h.Controller.Start();
        h.Sessions.ResignActive();
        h.Sessions.BecomeActive();
        Assert.False(h.Controller.IsActive);
    }

    // --- Schedule ---

    [Fact]
    public void ScheduleTriggerFollowsActiveHours()
    {
        var start = new FakeScheduler().Now.ToLocalTime();
        var minute = start.Hour * 60 + start.Minute;
        using var h = new ControllerHarness(s =>
        {
            s.ScheduleEnabled = true;
            s.ScheduleWindows = [new ScheduleWindow
            {
                Weekdays = [start.DayOfWeek],
                StartMinutes = minute + 1,
                EndMinutes = minute + 3,
            }];
        });
        h.Controller.Start();
        Assert.False(h.Controller.IsActive);

        h.Scheduler.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(ActivationSource.Schedule, h.Controller.CurrentSource);

        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.False(h.Controller.IsActive);
    }

    // --- CPU ---

    [Fact]
    public void CpuTriggerNeedsSustainedLoad()
    {
        using var h = new ControllerHarness(s =>
        {
            s.ActivateOnCpuLoad = true;
            s.CpuLoadThreshold = 50;
        });
        h.Cpu.BusyPercent = 90;
        h.Controller.Start();
        h.Scheduler.Advance(TimeSpan.FromSeconds(25));
        Assert.False(h.Controller.IsActive);
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ActivationSource.CpuLoad, h.Controller.CurrentSource);

        h.Cpu.BusyPercent = 10;
        h.Scheduler.Advance(TimeSpan.FromSeconds(35));
        Assert.False(h.Controller.IsActive);
    }

    // --- Audio ---

    [Fact]
    public void ExternalAudioTrigger()
    {
        using var h = new ControllerHarness(s => s.ActivateOnExternalAudioOutput = true);
        h.Controller.Start();
        h.Audio.External = true;
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(ActivationSource.AudioOutput, h.Controller.CurrentSource);

        h.Audio.External = null; // unknown counts as not external
        h.Scheduler.Advance(TimeSpan.FromSeconds(5));
        Assert.False(h.Controller.IsActive);
    }

    // --- Downloads ---

    [Fact]
    public void DownloadTrigger()
    {
        using var h = new ControllerHarness();
        var downloads = Path.Combine(h.Directory, "dl");
        Directory.CreateDirectory(downloads);
        h.Store.Update(s =>
        {
            s.DownloadInProgressActivationEnabled = true;
            s.DownloadDirectories = [downloads];
        });
        h.Controller.Start();
        Assert.False(h.Controller.IsActive);

        var partial = Path.Combine(downloads, "movie.mp4.crdownload");
        File.WriteAllText(partial, "x");
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(ActivationSource.Download, h.Controller.CurrentSource);

        File.Delete(partial);
        h.Scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void SwitchingOffEveryDownloadFolderStopsTheTrigger()
    {
        using var h = new ControllerHarness();
        var downloads = Path.Combine(h.Directory, "dl");
        Directory.CreateDirectory(downloads);
        File.WriteAllText(Path.Combine(downloads, "a.part"), "x");
        h.Store.Update(s =>
        {
            s.DownloadInProgressActivationEnabled = true;
            s.DownloadDirectories = [downloads];
        });
        h.Controller.Start();
        Assert.True(h.Controller.IsActive);

        h.Store.Update(s => s.DisabledDownloadDirectories = [downloads]);
        Assert.False(h.Controller.IsActive);
    }

    // --- Side effects ---

    [Fact]
    public void MouseJigglerRunsOnlyDuringSessionsAndFollowsTheSetting()
    {
        using var h = new ControllerHarness(s => s.MouseJiggler = true);
        h.Controller.Start();
        h.Scheduler.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, h.Nudger.Count);

        h.Controller.Activate(null);
        Assert.Equal(1, h.Nudger.Count);
        h.Scheduler.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Nudger.Count);

        h.Store.Update(s => s.MouseJiggler = false);
        h.Scheduler.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(2, h.Nudger.Count);

        h.Store.Update(s => s.MouseJiggler = true);
        Assert.Equal(3, h.Nudger.Count);
        h.Controller.Deactivate();
        h.Scheduler.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(3, h.Nudger.Count);
    }

    [Fact]
    public void AllowDisplaySleepChangeReappliesTheRequest()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(null);
        Assert.False(h.Assertion.AllowDisplaySleep);

        h.Store.Update(s => s.AllowDisplaySleep = true);
        Assert.True(h.Assertion.AllowDisplaySleep);
        Assert.True(h.Assertion.Held);
    }

    [Fact]
    public void NegativeDurationIsIgnored()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        h.Controller.Activate(-Second);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ExternalDisplayTriggerFollowsTheSetting()
    {
        using var h = new ControllerHarness();
        h.Displays.Set(1);
        h.Controller.Start();
        Assert.False(h.Controller.IsActive);

        h.Store.Update(s => s.ActivateOnExternalDisplay = true);
        Assert.Equal(ActivationSource.ExternalDisplay, h.Controller.CurrentSource);

        h.Store.Update(s => s.ActivateOnExternalDisplay = false);
        Assert.False(h.Controller.IsActive);
        h.Displays.Set(2);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void TriggerSessionsGetNoBatteryOverride()
    {
        using var h = new ControllerHarness(s =>
        {
            s.BatteryCapacityThresholdEnabled = true;
            s.BatteryCapacityThreshold = 20;
            s.WatchedApplications = ["a.exe"];
        });
        h.Power.Set(BatteryState.Unplugged, 10);
        h.Processes.Running.Add("a.exe");
        h.Controller.Start();
        Assert.True(h.Controller.IsActive);

        h.Power.Set(BatteryState.Unplugged, 9);
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void ReplacingASessionDoesNotAnnounceAllowingSleep()
    {
        using var h = new ControllerHarness(s => s.MouseJiggler = true);
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(1));
        h.Controller.Activate(TimeSpan.FromMinutes(2));
        Assert.Equal([true, true], h.Notifications.Select(n => n.Activating));
        h.Scheduler.Advance(TimeSpan.FromMinutes(1));
        Assert.True(h.Nudger.Count >= 2); // the jiggler survived the replacement
    }

    [Fact]
    public void AbsurdDurationsAreRejected()
    {
        using var h = new ControllerHarness();
        h.Controller.Start();
        Assert.Null(AppCommand.ParseUrl("keepingyouawake:///activate?hours=100000000"));
        h.Controller.Activate(TimeSpan.FromDays(10_000));
        Assert.False(h.Controller.IsActive);
        Assert.Null(h.Controller.CurrentSource);
    }

    [Fact]
    public void HandEditedHugeDurationsAreDropped()
    {
        using var h = new ControllerHarness(s =>
        {
            s.Durations = [60, long.MaxValue];
            s.DefaultDurationSeconds = long.MaxValue;
            s.ActivateOnLaunch = true;
            s.BatteryCapacityThreshold = double.NaN;
        });
        h.Controller.Start();
        Assert.Equal(ActivationDuration.Indefinite, h.Controller.CurrentDuration);
        Assert.DoesNotContain(new ActivationDuration(long.MaxValue), h.Controller.Durations.Durations);
        Assert.Equal(10, h.Store.Settings.EffectiveBatteryThreshold);
    }
}
