using System.Text.Json.Nodes;

namespace KeepingYouAwake.Core.Tests;

public class ActivationOwnershipTests
{
    [Fact]
    public void OnlyTheOwningSourceCanTerminate()
    {
        var ownership = new ActivationOwnership();
        Assert.False(ownership.TerminateIfOwnedBy(ActivationSource.User));

        ownership.Start(ActivationSource.WatchedApp);
        Assert.False(ownership.TerminateIfOwnedBy(ActivationSource.WatchedSsid));
        Assert.True(ownership.IsActive);
        Assert.True(ownership.TerminateIfOwnedBy(ActivationSource.WatchedApp));
        Assert.False(ownership.IsActive);
        Assert.Equal(ActivationSource.User, ownership.Source);
    }

    [Theory]
    [InlineData(ActivationSource.User, "user")]
    [InlineData(ActivationSource.WatchedApp, "watched-app")]
    [InlineData(ActivationSource.WatchedSsid, "watched-ssid")]
    [InlineData(ActivationSource.ACPower, "ac-power")]
    [InlineData(ActivationSource.ExternalDisplay, "external-display")]
    [InlineData(ActivationSource.Schedule, "schedule")]
    [InlineData(ActivationSource.Download, "download")]
    [InlineData(ActivationSource.AudioOutput, "audio-output")]
    [InlineData(ActivationSource.CpuLoad, "cpu-load")]
    public void LogStrings(ActivationSource source, string expected) => Assert.Equal(expected, source.ToLogString());
}

public class ActivationDurationTests
{
    [Fact]
    public void ControllerSeedsDefaultsAndPutsIndefiniteFirst()
    {
        var controller = new ActivationDurationsController(new SettingsStore(null));
        Assert.Equal(ActivationDuration.Indefinite, controller.Durations[0]);
        Assert.Equal(ActivationDuration.Defaults.Count + 1, controller.Durations.Count);
        Assert.Equal(ActivationDuration.Indefinite, controller.Default);
    }

    [Fact]
    public void AddRemoveAndDefaultFallback()
    {
        var controller = new ActivationDurationsController(new SettingsStore(null));
        var d = new ActivationDuration(42);
        Assert.True(controller.Add(d));
        Assert.False(controller.Add(d));
        Assert.False(controller.Add(ActivationDuration.Indefinite));
        Assert.Equal(d, controller.Durations[1]);

        controller.Default = d;
        Assert.Equal(d, controller.Default);
        Assert.True(controller.Remove(d));
        Assert.Equal(ActivationDuration.Indefinite, controller.Default);
        Assert.False(controller.Remove(ActivationDuration.Indefinite));
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var store = new SettingsStore(null);
        var controller = new ActivationDurationsController(store);
        controller.Add(new ActivationDuration(42));
        controller.Default = new ActivationDuration(42);
        controller.Reset();
        Assert.DoesNotContain(new ActivationDuration(42), controller.Durations);
        Assert.Equal(ActivationDuration.Indefinite, controller.Default);
    }

    [Theory]
    [InlineData(1, 30, 0, 5400)]
    [InlineData(0, 0, 59, 59)]
    [InlineData(100, 0, 0, 360000)]
    public void FromComponentsValid(int h, int m, int s, long expected) =>
        Assert.Equal(expected, ActivationDuration.FromComponents(h, m, s)!.Value.Seconds);

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 60, 0)]
    [InlineData(0, 0, 60)]
    [InlineData(-1, 0, 0)]
    public void FromComponentsInvalid(int h, int m, int s) => Assert.Null(ActivationDuration.FromComponents(h, m, s));
}

public class SettingsStoreTests
{
    [Fact]
    public void PersistsAndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), "kya-settings-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var changes = 0;
            store.Changed += (_, _) => changes++;
            store.Update(s =>
            {
                s.MouseJiggler = true;
                s.WatchedApplications = ["obs64.exe", " ", ""];
                s.ScheduleWindows = [new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 2000, EndMinutes = -5 }];
            });
            Assert.Equal(1, changes);

            var reloaded = new SettingsStore(path).Settings;
            Assert.True(reloaded.MouseJiggler);
            Assert.Equal(["obs64.exe"], reloaded.WatchedApplications);
            Assert.Equal(1439, reloaded.ScheduleWindows[0].StartMinutes);
            Assert.Equal(0, reloaded.ScheduleWindows[0].EndMinutes);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndIsKept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kya-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var store = new SettingsStore(path);
            Assert.True(store.Settings.ShowNotifications);
            Assert.True(File.Exists(path + ".invalid"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ThresholdsAreClamped()
    {
        var s = new AppSettings { BatteryCapacityThreshold = 3, CpuLoadThreshold = 150 };
        Assert.Equal(10, s.EffectiveBatteryThreshold);
        Assert.Equal(99, s.EffectiveCpuLoadThreshold);
        s.CpuLoadThreshold = double.NaN;
        Assert.Equal(50, s.EffectiveCpuLoadThreshold);
    }

    [Fact]
    public void DownloadDirectoriesDefaultToDownloadsFolder()
    {
        var s = new AppSettings();
        Assert.Equal([WatchedItems.DefaultDownloadsFolder], s.EnabledDownloadDirectories());
        s.DownloadDirectories = ["C:\\A"];
        s.DisabledDownloadDirectories = ["c:\\a"];
        Assert.Empty(s.EnabledDownloadDirectories());
    }

    [Fact]
    public void ResetAdvancedLeavesOtherSettingsAlone()
    {
        var s = new AppSettings { MouseJiggler = true, ActivateOnLaunch = true, CpuLoadThreshold = 80 };
        s.ResetAdvanced();
        Assert.False(s.MouseJiggler);
        Assert.Equal(50, s.CpuLoadThreshold);
        Assert.True(s.ActivateOnLaunch);
    }
}

public class WatchedItemsTests
{
    [Theory]
    [InlineData(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "obs64.exe")]
    [InlineData("obs64", "obs64.exe")]
    [InlineData("\"Teams.EXE\"", "Teams.EXE")]
    public void NormalizesExecutableNames(string input, string expected) =>
        Assert.Equal(expected, WatchedItems.NormalizeExecutableName(input));

    [Fact]
    public void MatchesRunningProcessesCaseInsensitively()
    {
        Assert.True(WatchedItems.AnyRunning(["Zoom"], ["explorer.exe", "zoom.exe"]));
        Assert.False(WatchedItems.AnyRunning(["Zoom"], ["zoomit.exe"]));
        Assert.False(WatchedItems.AnyRunning([], ["zoom.exe"]));
    }

    [Fact]
    public void EnabledFiltersDisabledEntries() =>
        Assert.Equal(["b"], WatchedItems.Enabled(["A", "b"], ["a"], ignoreCase: true));
}

public class ScheduleWindowTests
{
    private static DateTime At(DayOfWeek day, int hour, int minute)
    {
        var date = new DateTime(2026, 10, 18); // a Sunday
        return date.AddDays((int)day).AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void SameDayWindowIsHalfOpen()
    {
        var w = new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 9 * 60, EndMinutes = 17 * 60 };
        Assert.True(w.Contains(At(DayOfWeek.Monday, 9, 0)));
        Assert.True(w.Contains(At(DayOfWeek.Monday, 16, 59)));
        Assert.False(w.Contains(At(DayOfWeek.Monday, 17, 0)));
        Assert.False(w.Contains(At(DayOfWeek.Tuesday, 10, 0)));
    }

    [Fact]
    public void WrappingWindowContinuesIntoTheNextDay()
    {
        var w = new ScheduleWindow { Weekdays = [DayOfWeek.Saturday], StartMinutes = 22 * 60, EndMinutes = 2 * 60 };
        Assert.True(w.Contains(At(DayOfWeek.Saturday, 23, 0)));
        Assert.True(w.Contains(At(DayOfWeek.Sunday, 1, 59)));
        Assert.False(w.Contains(At(DayOfWeek.Sunday, 2, 0)));
        Assert.False(w.Contains(At(DayOfWeek.Saturday, 1, 0)));
    }

    [Fact]
    public void DisabledOrEmptyWindowsNeverMatch()
    {
        Assert.False(new ScheduleWindow { Weekdays = [], StartMinutes = 0, EndMinutes = 100 }.Contains(At(DayOfWeek.Monday, 0, 30)));
        Assert.False(new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 0, EndMinutes = 100, Enabled = false }
            .Contains(At(DayOfWeek.Monday, 0, 30)));
    }
}

public class SleepWakeTimerTests
{
    [Fact]
    public void ExpiryReportsNotCancelledAndReleases()
    {
        var scheduler = new FakeScheduler();
        var assertion = new FakeSleepAssertion();
        var timer = new SleepWakeTimer(assertion, scheduler);
        bool? cancelled = null;
        timer.Schedule(TimeSpan.FromSeconds(10), allowDisplaySleep: true, c => cancelled = c);
        Assert.Equal(scheduler.Now.AddSeconds(10), timer.FireDate);
        Assert.True(assertion.AllowDisplaySleep);

        scheduler.Advance(TimeSpan.FromSeconds(10));
        Assert.False(cancelled);
        Assert.False(timer.IsScheduled);
        Assert.False(assertion.Held);
        Assert.Equal(0, scheduler.ActiveTimers);
    }

    [Fact]
    public void InvalidateReportsCancelledOnce()
    {
        var timer = new SleepWakeTimer(new FakeSleepAssertion(), new FakeScheduler());
        var calls = new List<bool>();
        timer.Schedule(null, false, calls.Add);
        Assert.Null(timer.FireDate);
        timer.Invalidate();
        timer.Invalidate();
        Assert.Equal([true], calls);
    }
}

public class AppCommandTests
{
    [Theory]
    [InlineData("keepingyouawake:///activate?seconds=90", 90)]
    [InlineData("keepingyouawake://activate?minutes=1.2", 120)]
    [InlineData("KEEPINGYOUAWAKE:///activate?hours=2", 7200)]
    [InlineData("keepingyouawake:///activate?seconds=5&minutes=10", 5)]
    public void ParsesActivateDurations(string url, double seconds)
    {
        var command = AppCommand.ParseUrl(url)!;
        Assert.Equal(AppCommandKind.Activate, command.Kind);
        Assert.Equal(TimeSpan.FromSeconds(seconds), command.Duration);
        Assert.False(command.UseDefaultDuration);
    }

    [Theory]
    [InlineData("keepingyouawake:///activate?seconds=-1")]
    [InlineData("keepingyouawake:///activate?seconds=abc")]
    [InlineData("keepingyouawake:///unknown")]
    [InlineData("https://example.com/activate")]
    [InlineData("not a url")]
    public void RejectsInvalidUrls(string url) => Assert.Null(AppCommand.ParseUrl(url));

    [Fact]
    public void ParsesOtherActions()
    {
        Assert.True(AppCommand.ParseUrl("keepingyouawake:///activate")!.UseDefaultDuration);
        Assert.Equal(AppCommandKind.Deactivate, AppCommand.ParseUrl("keepingyouawake:///deactivate")!.Kind);
        Assert.Equal(AppCommandKind.Toggle, AppCommand.ParseUrl("keepingyouawake:///toggle/")!.Kind);
    }

    [Fact]
    public void ParsesArguments()
    {
        Assert.Null(AppCommand.ParseArguments([]));
        Assert.Equal(TimeSpan.FromSeconds(30), AppCommand.ParseArguments(["--activate", "30"])!.Duration);
        Assert.True(AppCommand.ParseArguments(["--activate"])!.UseDefaultDuration);
        Assert.Equal(AppCommandKind.Toggle, AppCommand.ParseArguments(["/toggle"])!.Kind);
        Assert.Equal(AppCommandKind.Deactivate, AppCommand.ParseArguments(["keepingyouawake:///deactivate"])!.Kind);
        Assert.Null(AppCommand.ParseArguments(["--bogus"]));
    }
}

public class ActivityLoggerTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "kya-log-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private ActivityLogger NewLogger(int max = 1000) =>
        new(Path.Combine(dir, "activity.jsonl"), max, () => now);

    public void Dispose()
    {
        if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WritesTheMacCompatibleFormat()
    {
        var log = NewLogger();
        log.RecordStarted("user", 300);
        now = now.AddMinutes(5);
        log.RecordEnded(ActivityLogEndedReasons.Expired);

        var line = File.ReadAllLines(log.FilePath).Single();
        var obj = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("2026-10-21T12:00:00Z", (string?)obj["startedAt"]);
        Assert.Equal("2026-10-21T12:05:00Z", (string?)obj["endedAt"]);
        Assert.Equal("user", (string?)obj["source"]);
        Assert.Equal(300, (double)obj["requestedDuration"]!);
        Assert.Equal("expired", (string?)obj["endedReason"]);
    }

    [Fact]
    public void RecentEntriesAreNewestFirstAndCapped()
    {
        var log = NewLogger(max: 3);
        for (var i = 0; i < 5; i++)
        {
            log.RecordStarted("user", i);
            log.RecordEnded(ActivityLogEndedReasons.UserCancelled);
            now = now.AddMinutes(1);
        }
        var entries = log.RecentEntries(10);
        Assert.Equal([4.0, 3.0, 2.0], entries.Select(e => e.RequestedDuration));
        Assert.Single(log.RecentEntries(1));
    }

    [Fact]
    public void EndWithoutOpenEntryIsANoOp()
    {
        var log = NewLogger();
        log.RecordEnded(ActivityLogEndedReasons.UserCancelled);
        Assert.False(File.Exists(log.FilePath));
    }

    [Fact]
    public void StartingAgainClosesAnUnrecordedOpenEntryAsExpired()
    {
        var log = NewLogger();
        log.RecordStarted("schedule", -1);
        now = now.AddMinutes(1);
        log.RecordStarted("user", 60);
        var entries = log.RecentEntries(2);
        Assert.Null(entries[0].EndedAt);
        Assert.Equal(ActivityLogEndedReasons.Expired, entries[1].EndedReason);
    }

    [Fact]
    public void CloseDanglingRepairsEveryOpenEntryAndSkipsCorruptLines()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "activity.jsonl");
        File.WriteAllLines(path,
        [
            """{"startedAt":"2026-10-20T10:00:00Z","source":"user","requestedDuration":-1}""",
            "garbage",
            """{"startedAt":"2026-10-20T11:00:00Z","source":"watched-app","requestedDuration":-1}""",
        ]);
        var log = NewLogger();
        log.CloseDanglingEntries(ActivityLogEndedReasons.AppTerminated);
        var entries = log.RecentEntries(10);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(ActivityLogEndedReasons.AppTerminated, e.EndedReason));
        Assert.All(entries, e => Assert.Equal(now, e.EndedAt));
    }
}

public class UpdateCheckerTests
{
    private const string Releases = """
    [
      {"tag_name":"v1.3.0-beta.2","prerelease":true,"draft":false,"html_url":"https://x/1.3.0-beta.2"},
      {"tag_name":"v1.2.0","prerelease":false,"draft":false,"html_url":"https://x/1.2.0"},
      {"tag_name":"v9.0.0","prerelease":false,"draft":true,"html_url":"https://x/draft"},
      {"tag_name":"v1.1.0","prerelease":false,"draft":false,"html_url":"https://x/1.1.0"},
      {"tag_name":"nightly","prerelease":false,"draft":false,"html_url":"https://x/nightly"}
    ]
    """;

    [Fact]
    public void PicksTheNewestStableRelease()
    {
        var update = UpdateChecker.SelectUpdate(Releases, "1.0.0", includePreReleases: false)!;
        Assert.Equal("v1.2.0", update.Tag);
        Assert.Equal("https://x/1.2.0", update.PageUrl);
    }

    [Fact]
    public void IncludesPreReleasesWhenAsked() =>
        Assert.Equal("v1.3.0-beta.2", UpdateChecker.SelectUpdate(Releases, "1.2.0", includePreReleases: true)!.Tag);

    [Fact]
    public void UpToDateReturnsNull()
    {
        Assert.Null(UpdateChecker.SelectUpdate(Releases, "1.2.0", includePreReleases: false));
        Assert.Null(UpdateChecker.SelectUpdate(Releases, "1.3.0", includePreReleases: true));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0-beta.1", 1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("2.0.0", "1.9.9", 1)]
    public void SemVerPrecedence(string a, string b, int expected)
    {
        var pa = UpdateChecker.ParseVersion(a)!.Value;
        var pb = UpdateChecker.ParseVersion(b)!.Value;
        Assert.Equal(expected, Math.Sign(UpdateChecker.Compare(pa.Version, pa.Label, pb.Version, pb.Label)));
    }
}

[Collection(StringsCollection.Name)]
public class StringsTests
{
    [Fact]
    public void EveryKeyHasBothLanguages()
    {
        try
        {
            foreach (var key in Strings.Keys.Where(k => k != "Unit.separator"))
            {
                Strings.OverrideLanguage("en");
                Assert.False(string.IsNullOrWhiteSpace(Strings.Get(key)), key);
                Strings.OverrideLanguage("ja");
                Assert.False(string.IsNullOrWhiteSpace(Strings.Get(key)), key);
            }
        }
        finally
        {
            Strings.OverrideLanguage(null);
        }
    }

    [Fact]
    public void FormatsDurations()
    {
        try
        {
            Strings.OverrideLanguage("en");
            Assert.Equal("1 hour, 30 minutes", Strings.DurationText(TimeSpan.FromMinutes(90)));
            Assert.Equal("5 minutes", Strings.DurationTitle(ActivationDuration.FromMinutes(5)));
            Assert.Equal("Indefinitely", Strings.DurationTitle(ActivationDuration.Indefinite));
            Strings.OverrideLanguage("ja");
            Assert.Equal("1時間30分", Strings.DurationText(TimeSpan.FromMinutes(90)));
            Assert.Equal("26時間", Strings.DurationText(TimeSpan.FromHours(26)));
        }
        finally
        {
            Strings.OverrideLanguage(null);
        }
    }

    [Theory]
    [InlineData(3909, "1:05:09")]
    [InlineData(299.2, "5:00")]
    [InlineData(-3, "0:00")]
    public void FormatsCountdown(double seconds, string expected) =>
        Assert.Equal(expected, Strings.Countdown(TimeSpan.FromSeconds(seconds)));
}

public class SettingsReloadTests
{
    [Fact]
    public void ReloadAppliesOutsideEditsOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kya-reload-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var changes = 0;
            store.Changed += (_, _) => changes++;
            store.Update(s => s.ShowNotifications = false);
            Assert.False(store.ReloadFromDisk()); // our own save
            Assert.Equal(1, changes);

            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            json["mouseJiggler"] = true;
            json["CpuLoadThreshold"] = 75; // property names are case-insensitive
            File.WriteAllText(path, json.ToJsonString());
            Assert.True(store.ReloadFromDisk());
            Assert.True(store.Settings.MouseJiggler);
            Assert.Equal(75, store.Settings.CpuLoadThreshold);
            Assert.False(store.Settings.ShowNotifications);
            Assert.Equal(2, changes);

            File.WriteAllText(path, "{ broken");
            Assert.False(store.ReloadFromDisk());
            Assert.True(store.Settings.MouseJiggler);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    }
}
