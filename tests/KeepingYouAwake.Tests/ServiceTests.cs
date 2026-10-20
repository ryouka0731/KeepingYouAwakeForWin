using System.Net;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;
using Microsoft.Win32;

namespace KeepingYouAwake.Tests;

/// <summary>A GitHub releases endpoint that counts requests and can be held open.</summary>
internal sealed class FakeReleasesHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public int Requests;
    public TaskCompletionSource? Gate { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Requests);
        if (Gate is { } gate) { await gate.Task; }
        return new HttpResponseMessage(status) { Content = new StringContent(json) };
    }
}

public class UpdateServiceTests
{
    private const string NewerRelease = """[{"tag_name":"v99.0.0","html_url":"https://example.invalid/v99"}]""";

    [Fact]
    public async Task CheckNowRecordsTheCheckAndFindsANewerRelease()
    {
        var store = new SettingsStore(null);
        using var updates = new UpdateService(store, new FakeReleasesHandler(NewerRelease));
        var release = await updates.CheckNowAsync();
        Assert.Equal("v99.0.0", release!.Tag);
        Assert.NotNull(store.Settings.LastUpdateCheck);
    }

    [Fact]
    public async Task ConcurrentChecksShareOneRequest()
    {
        var handler = new FakeReleasesHandler(NewerRelease) { Gate = new TaskCompletionSource() };
        using var updates = new UpdateService(new SettingsStore(null), handler);
        var first = updates.CheckNowAsync();
        var second = updates.CheckNowAsync();
        handler.Gate.SetResult();
        Assert.Equal((await first)!.Tag, (await second)!.Tag);
        Assert.Equal(1, handler.Requests);

        await updates.CheckNowAsync(); // a later check makes a new request
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task BackgroundCheckRespectsTheSettingAndTheDailyInterval()
    {
        var store = new SettingsStore(null);
        var handler = new FakeReleasesHandler(NewerRelease);
        using var updates = new UpdateService(store, handler);
        ReleaseInfo? found = null;
        updates.UpdateFound += (_, r) => found = r;

        store.Update(s => s.AutomaticallyCheckForUpdates = false);
        await updates.CheckIfDueAsync();
        Assert.Equal(0, handler.Requests);

        store.Update(s => s.AutomaticallyCheckForUpdates = true);
        await updates.CheckIfDueAsync();
        Assert.Equal(1, handler.Requests);
        Assert.Equal("v99.0.0", found?.Tag);

        await updates.CheckIfDueAsync(); // checked moments ago
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task BackgroundCheckSwallowsNetworkErrors()
    {
        var store = new SettingsStore(null);
        using var updates = new UpdateService(store, new FakeReleasesHandler("nope", HttpStatusCode.ServiceUnavailable));
        await updates.CheckIfDueAsync();
        Assert.Null(store.Settings.LastUpdateCheck); // retried later
        await Assert.ThrowsAsync<HttpRequestException>(() => updates.CheckNowAsync());
    }

    [Fact]
    public void CurrentVersionHasThreeParts() =>
        Assert.Matches(@"^\d+\.\d+\.\d+", UpdateService.CurrentVersion);
}

/// <summary>OS notifications reaching the app's handlers.</summary>
public class SystemEventHandlerTests
{
    [Fact]
    public void SessionSwitchReasonsMapToActiveAndResigned() => Sta.Run(() =>
    {
        using var sessions = new WindowsSessionEvents();
        var events = new List<string>();
        sessions.SessionBecameActive += (_, _) => events.Add("active");
        sessions.SessionResignedActive += (_, _) => events.Add("resigned");
        foreach (var reason in new[]
                 {
                     SessionSwitchReason.ConsoleDisconnect, SessionSwitchReason.ConsoleConnect,
                     SessionSwitchReason.RemoteDisconnect, SessionSwitchReason.RemoteConnect,
                     SessionSwitchReason.SessionLock,
                 })
        {
            sessions.OnSessionSwitch(sessions, new SessionSwitchEventArgs(reason));
        }
        Sta.PumpUntil(() => events.Count == 4);
        Sta.Pump(50);
        Assert.Equal(["resigned", "active", "resigned", "active"], events);
    });

    [Fact]
    public void PowerAndDisplayNotificationsRefreshQuietlyWhenNothingChanged() => Sta.Run(() =>
    {
        using var power = new WindowsPowerSource(new WinFormsScheduler());
        var changes = 0;
        power.Changed += (_, _) => changes++;
        power.OnPowerModeChanged(power, new PowerModeChangedEventArgs(PowerModes.StatusChange));
        power.Refresh();
        Sta.Pump(50);
        Assert.Equal(0, changes); // same status as at construction

        using var displays = new DisplayConfigMonitor();
        var displayChanges = 0;
        displays.Changed += (_, _) => displayChanges++;
        displays.OnDisplaySettingsChanged(displays, EventArgs.Empty);
        Sta.Pump(50);
        Assert.Equal(0, displayChanges);
    });

    [Fact]
    public void TrayReactsToThemeNetworkAndSignOut() => Sta.Run(() =>
    {
        using var tray = new TrayHarness(s => s.WatchedWiFiSsids = ["Office"]);
        tray.Context.OnUserPreferenceChanged(tray, new UserPreferenceChangedEventArgs(UserPreferenceCategory.Color));
        tray.Context.OnUserPreferenceChanged(tray, new UserPreferenceChangedEventArgs(UserPreferenceCategory.Mouse)); // ignored
        Sta.Pump(50);

        tray.Fakes.WiFi.Ssids.Add("office");
        tray.Context.OnNetworkAddressChanged(tray, EventArgs.Empty); // re-checks Wi-Fi right away
        Sta.PumpUntil(() => tray.Controller.CurrentSource == ActivationSource.WatchedSsid);

        tray.Context.OnSessionEnding(tray, new SessionEndingEventArgs(SessionEndReasons.Logoff));
        Sta.PumpUntil(() => tray.Context.IsExiting);
        Assert.Equal(ActivityLogEndedReasons.AppTerminated, tray.Fakes.Log.RecentEntries(1)[0].EndedReason);
    });
}

public class TrayIntegrationTests
{
    [Fact]
    public void RealWindowsServicesStartAndStop() => Sta.Run(() =>
    {
        using var registry = new RegistrySandbox();
        var dir = Path.Combine(Path.GetTempPath(), "kya-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(dir, "settings.json"));
            var log = new ActivityLogger(Path.Combine(dir, "activity.jsonl"));
            using var tray = new TrayApplicationContext(store, log, new TrayOptions(Shell: registry.Shell, BackgroundUpdateChecks: false));
            tray.Controller.Activate(TimeSpan.FromMinutes(1));
            Assert.True(tray.Controller.IsActive);
            tray.Handle(["--quit"]);
            Assert.True(tray.IsExiting);
            Assert.Equal(ActivityLogEndedReasons.AppTerminated, log.RecentEntries(1)[0].EndedReason);
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    });

    [Fact]
    public void OutsideEditsOfSettingsJsonAreApplied() => Sta.Run(() =>
    {
        using var registry = new RegistrySandbox();
        var dir = Path.Combine(Path.GetTempPath(), "kya-watch-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var store = new SettingsStore(path);
            store.Update(s => s.ShowNotifications = true);
            using var tray = new TrayApplicationContext(store, new ActivityLogger(Path.Combine(dir, "a.jsonl")),
                new TrayOptions(Shell: registry.Shell, BackgroundUpdateChecks: false));

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"mouseJiggler\": false", "\"mouseJiggler\": true"));
            Sta.PumpUntil(() => store.Settings.MouseJiggler, 10_000);
            tray.ExitThread();
        }
        finally
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); }
        }
    });

    [Fact]
    public void UpdateFoundShowsANotificationLinkingTheRelease() => Sta.Run(() =>
    {
        var handler = new FakeReleasesHandler("""[{"tag_name":"v99.0.0","html_url":"https://example.invalid/v99"}]""");
        using var tray = new TrayHarness(updateHandler: handler);
        var check = tray.Context.Updates.CheckIfDueAsync();
        Sta.PumpUntil(() => check.IsCompleted);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("https://example.invalid/v99", tray.Context.PendingUpdateUrl);
        Assert.Equal(Strings.Get("UpdateAvailableTitle"), tray.Context.LastNotification?.Title);
    });
}
