using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core.Tests;

/// <summary>Write failures, asynchronous I/O and lifecycle edges that the happy-path tests don't reach.</summary>
public class FailureAndEdgeCaseTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "kya-edge-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public FailureAndEdgeCaseTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(dir, recursive: true);
    }

    private string LogPath => Path.Combine(dir, "activity.jsonl");

    /// <summary>Blocks the logger's atomic write: a directory where its temp file must go.</summary>
    private IDisposable BlockLogWrites()
    {
        var blocker = Directory.CreateDirectory(LogPath + ".tmp");
        return new Unblock(() => blocker.Delete());
    }

    private sealed class Unblock(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public void FailedEndWriteIsRecordedWithTheNextSession()
    {
        var log = new ActivityLogger(LogPath, clock: () => now);
        log.RecordStarted("user", 60);
        var endedAt = now = now.AddMinutes(1);
        using (BlockLogWrites())
        {
            log.RecordEnded(ActivityLogEndedReasons.UserCancelled);
        }
        Assert.Null(log.RecentEntries(1)[0].EndedAt); // not on disk yet

        now = now.AddMinutes(5);
        log.RecordStarted("schedule", -1);
        var entries = log.RecentEntries(2);
        Assert.Equal(endedAt, entries[1].EndedAt); // the real end, not the next start
        Assert.Equal(ActivityLogEndedReasons.UserCancelled, entries[1].EndedReason);
        Assert.Null(entries[0].EndedAt);
    }

    [Fact]
    public void FailedDanglingRepairIsRetriedOnTheNextAppend()
    {
        File.WriteAllText(LogPath, """{"startedAt":"2026-10-20T10:00:00Z","source":"user","requestedDuration":-1}""" + "\n");
        var log = new ActivityLogger(LogPath, clock: () => now);
        using (BlockLogWrites())
        {
            log.CloseDanglingEntries(ActivityLogEndedReasons.AppTerminated);
        }
        Assert.Null(log.RecentEntries(1)[0].EndedAt);

        log.RecordStarted("user", 30);
        var old = log.RecentEntries(2)[1];
        Assert.Equal(ActivityLogEndedReasons.AppTerminated, old.EndedReason);
        Assert.Equal(now, old.EndedAt);
    }

    [Fact]
    public void FailedStartLeavesNoOpenEntry()
    {
        var log = new ActivityLogger(LogPath, clock: () => now);
        using (BlockLogWrites())
        {
            log.RecordStarted("user", 30);
        }
        log.RecordEnded(ActivityLogEndedReasons.Expired); // nothing open: no-op
        Assert.Empty(log.RecentEntries(5));
    }

    [Fact]
    public void OpenEntryTrimmedByTheCapIsForgotten()
    {
        var log = new ActivityLogger(LogPath, maximumEntries: 1, clock: () => now);
        log.RecordStarted("user", 1);
        File.WriteAllText(LogPath, string.Empty); // the file was emptied behind the logger's back
        log.RecordEnded(ActivityLogEndedReasons.Expired);
        log.RecordStarted("user", 2);
        Assert.Single(log.RecentEntries(5));
    }

    [Fact]
    public void ParseDefaultsForMissingOrWrongTypedFields()
    {
        var entry = ActivityLogger.Parse(System.Text.Json.Nodes.JsonNode.Parse(
            """{"startedAt":"2026-10-21T12:00:00Z","endedAt":5,"source":7,"requestedDuration":"x","endedReason":false}""")!.AsObject())!;
        Assert.Null(entry.EndedAt);
        Assert.Equal(ActivityLogSources.User, entry.Source);
        Assert.Equal(-1, entry.RequestedDuration);
        Assert.Null(entry.EndedReason);
        Assert.Null(ActivityLogger.Parse(System.Text.Json.Nodes.JsonNode.Parse("""{"startedAt":"yesterday"}""")!.AsObject()));
    }

    [Fact]
    public void SettingsSaveFailureKeepsSettingsInMemory()
    {
        var blockedParent = Path.Combine(dir, "not-a-directory");
        File.WriteAllText(blockedParent, "x");
        var store = new SettingsStore(Path.Combine(blockedParent, "settings.json"));
        store.Update(s => s.MouseJiggler = true);
        Assert.True(store.Settings.MouseJiggler);
        Assert.False(store.ReloadFromDisk());
        Assert.False(new SettingsStore(null).ReloadFromDisk());
    }

    [Fact]
    public void DriveAliveRunsDiskIoInTheBackgroundAndCleansUp()
    {
        var scheduler = new FakeScheduler();
        var drives = new FakeDriveEnumerator();
        var external = Directory.CreateDirectory(Path.Combine(dir, "ext")).FullName;
        drives.Roots.Add(external);
        drives.Roots.Add(Path.Combine(dir, "gone")); // ejected drive: write fails quietly
        var timer = new DriveAliveTimer(scheduler, drives, dir) { Interval = TimeSpan.FromSeconds(30) };

        timer.Start();
        timer.Start(); // idempotent
        scheduler.Advance(TimeSpan.FromSeconds(30));
        timer.Flush(TimeSpan.FromSeconds(5));
        var ping = Path.Combine(external, DriveAliveTimer.PingFileName);
        Assert.True(File.Exists(ping));

        timer.Dispose();
        timer.Stop(); // idempotent
        timer.Flush(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(ping));
        Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public void DriveAliveLeavesAFileTheNextSessionRewroteAlone()
    {
        var drives = new FakeDriveEnumerator();
        drives.Roots.Add(dir);
        var timer = new DriveAliveTimer(new FakeScheduler(), drives, dir, runInline: true);
        timer.Start();
        var ping = Path.Combine(dir, DriveAliveTimer.PingFileName);
        File.SetAttributes(ping, FileAttributes.Normal); // Windows refuses to overwrite hidden files
        File.WriteAllText(ping, DriveAliveTimer.ContentPrefix + "someone-else 2026\n");
        timer.Stop();
        Assert.True(File.Exists(ping));
    }

    [Fact]
    public void ReadHeadHandlesMissingAndLinkedPaths()
    {
        Assert.Null(DriveAliveTimer.ReadHead(Path.Combine(dir, "missing")));
        var target = Path.Combine(dir, "target");
        File.WriteAllText(target, "keepingyouawake x");
        var link = Path.Combine(dir, "link");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // no symlink privilege
        Assert.Null(DriveAliveTimer.ReadHead(link));
        Assert.False(DriveAliveTimer.WritePing(link, "keepingyouawake y\n"u8.ToArray(), hidden: false));
        Assert.Equal("keepingyouawake x", File.ReadAllText(target));
    }

    [Fact]
    public void ControllerCommandAndLifecycleEdges()
    {
        using var h = new ControllerHarness(s => s.DefaultDurationSeconds = 600);
        h.Controller.Start();
        h.Controller.Handle(new AppCommand(AppCommandKind.Toggle));
        Assert.True(h.Controller.IsActive);
        h.Controller.Handle(new AppCommand(AppCommandKind.Deactivate));
        Assert.False(h.Controller.IsActive);
        h.Controller.Handle(new AppCommand(AppCommandKind.ShowSettings)); // UI-only: ignored
        Assert.False(h.Controller.IsActive);
        h.Controller.Handle(new AppCommand(AppCommandKind.Activate, TimeSpan.FromMinutes(1)));
        Assert.Equal(new ActivationDuration(60), h.Controller.CurrentDuration);

        h.Controller.Shutdown();
        h.Controller.Shutdown(); // idempotent
        h.Controller.Dispose();
        h.Store.Update(s => s.MouseJiggler = true); // no longer observed
        h.Controller.NetworkChanged();
        Assert.False(h.Assertion.Held);
    }

    [Fact]
    public void DiskKeepAliveFollowsBothSettings()
    {
        using var h = new ControllerHarness(s => s.PreventDiskSleep = true);
        var external = Directory.CreateDirectory(Path.Combine(dir, "ext2")).FullName;
        h.Drives.Roots.Add(external);
        h.Controller.Start();
        h.Controller.Activate(null);
        h.Store.Update(s => s.DriveAlive = true); // switches to the timer that also covers other drives
        h.Store.Update(s => { s.DriveAlive = false; s.PreventDiskSleep = false; });
        h.Controller.Deactivate();
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void TriggerEndingWhileSuspendedForUserSwitchDoesNotResume()
    {
        using var h = new ControllerHarness(s =>
        {
            s.DeactivateOnUserSwitch = true;
            s.WatchedApplications = ["a.exe"];
        });
        h.Processes.Running.Add("a.exe");
        h.Controller.Start();
        h.Sessions.ResignActive();
        h.Processes.Running.Clear();
        h.Scheduler.Advance(TimeSpan.FromSeconds(5)); // the app quit while we were away
        h.Sessions.BecomeActive();
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void PowerChangesWithoutASessionOnlyFeedTheACTrigger()
    {
        using var h = new ControllerHarness(s => s.DeactivateOnFullCharge = true);
        h.Controller.Start();
        h.Power.Set(BatteryState.Full, 100);
        Assert.False(h.Controller.IsActive);
        h.Store.Update(s => s.ScheduleEnabled = true); // enabled without windows: stays off
        h.Store.Update(s => s.DownloadInProgressActivationEnabled = true);
        h.Store.Update(s => s.DownloadDirectories = []); // every folder switched off
        Assert.False(h.Controller.IsActive);
    }

    [Fact]
    public void DownloadMonitorSkipsBlankDirectoriesAndStopsCleanly()
    {
        var monitor = new DownloadActivityMonitor(new FakeScheduler());
        monitor.SetDirectories(["  ", dir]);
        Assert.False(monitor.HasInProgressDownload());
        monitor.Start();
        monitor.Start();
        monitor.Dispose();
        Assert.False(monitor.IsRunning);
    }

    [Fact]
    public void ScheduleMonitorWithNoWindowsAndRestart()
    {
        var scheduler = new FakeScheduler();
        var monitor = new ScheduleMonitor(scheduler, _ => new DateTime(2026, 10, 19, 10, 0, 0)); // a Monday
        var entered = 0;
        monitor.Entered += (_, _) => entered++;
        monitor.Start();
        monitor.Start();
        monitor.SetWindows([new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 0, EndMinutes = 1439 }]);
        Assert.Equal(1, entered);
        monitor.Dispose();
        Assert.False(monitor.IsRunning);
        Assert.False(new ScheduleWindow { Weekdays = null!, StartMinutes = 0, EndMinutes = 10 }.Contains(DateTime.Now));
        Assert.Empty(new ScheduleWindow { Weekdays = null!, StartMinutes = 0, EndMinutes = 10 }.Normalized().Weekdays);
    }

    [Fact]
    public void SleepWakeTimerReacquireAndReschedule()
    {
        var assertion = new FakeSleepAssertion();
        var timer = new SleepWakeTimer(assertion, new FakeScheduler());
        timer.Reacquire(true); // nothing scheduled: no-op
        Assert.Equal(0, assertion.AcquireCount);
        var ended = new List<bool>();
        timer.Schedule(TimeSpan.FromMinutes(1), false, ended.Add);
        timer.Schedule(TimeSpan.Zero, false, null); // replaces; zero = indefinite
        Assert.Equal([true], ended);
        Assert.Null(timer.ScheduledInterval);
        timer.Reacquire(true);
        Assert.True(assertion.AllowDisplaySleep);
    }

    [Fact]
    public void JigglerAndCpuMonitorAreIdempotent()
    {
        var scheduler = new FakeScheduler();
        var nudger = new FakeNudger();
        using var jiggler = new MouseJiggler(scheduler, nudger);
        jiggler.Start();
        jiggler.Start();
        Assert.Equal(1, nudger.Count);

        var cpu = new CpuLoadMonitor(scheduler, new NullSampler());
        cpu.Start();
        cpu.Start();
        scheduler.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(cpu.CurrentBusyPercent);
        cpu.Dispose();
    }

    private sealed class NullSampler : ICpuTimesSampler
    {
        public CpuTimes? Sample() => null;
    }

    [Fact]
    public void ProjectUrlPointsAtTheRepository() =>
        Assert.Equal("https://github.com/" + UpdateChecker.DefaultRepository, UpdateChecker.ProjectUrl);
}
