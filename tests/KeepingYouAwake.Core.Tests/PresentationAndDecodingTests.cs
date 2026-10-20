using System.Net;
using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core.Tests;

public class WindowsDecodingTests
{
    [Theory]
    // acLine, flag, percent, status → state, capacity, saver
    [InlineData(0, 1, 80, 0, BatteryState.Unplugged, 80, false)]
    [InlineData(1, 8, 55, 0, BatteryState.Charging, 55, false)]
    [InlineData(1, 8, 100, 0, BatteryState.Full, 100, false)]   // "charging" at 100 % is full
    [InlineData(1, 1, 80, 0, BatteryState.Full, 80, false)]     // held at a charge limit
    [InlineData(0, 2, 15, 1, BatteryState.Unplugged, 15, true)] // battery saver
    [InlineData(1, 128, 255, 0, BatteryState.Unknown, 100, false)] // desktop
    [InlineData(1, 255, 50, 0, BatteryState.Unknown, 100, false)]  // unknown flag
    [InlineData(255, 1, 50, 0, BatteryState.Unknown, 100, false)]  // unknown AC line
    [InlineData(0, 1, 255, 0, BatteryState.Unknown, 100, false)]   // unknown percentage
    public void DecodesPowerStatus(byte ac, byte flag, byte percent, byte status, BatteryState state, double capacity, bool saver)
    {
        var decoded = WindowsDecoding.DecodePowerStatus(ac, flag, percent, status);
        Assert.Equal(new PowerStatus(state, capacity, saver), decoded);
        Assert.Equal(state is BatteryState.Charging or BatteryState.Full, decoded.IsOnACPower);
    }

    [Fact]
    public void CountsOnlyExternalDisplays()
    {
        // HDMI (5), DisplayPort (10), USB/indirect wired (16) are external;
        // internal, LVDS, eDP, UDI embedded, indirect virtual and "other" are not.
        uint[] technologies = [5, 10, 16, 0x80000000, 6, 11, 13, 17, 0xFFFFFFFF];
        Assert.Equal(3, WindowsDecoding.CountExternalDisplays(technologies));
        Assert.Equal(0, WindowsDecoding.CountExternalDisplays([]));
    }

    [Theory]
    [InlineData("USB", 1u, true)]
    [InlineData("BTHENUM", 3u, true)]
    [InlineData("bthhfenum", 5u, true)]
    [InlineData("BTHLEDevice", null, true)]
    [InlineData("HDAUDIO", 1u, false)]
    [InlineData("HDAUDIO", 3u, false)]
    [InlineData("HDAUDIO", 9u, true)]   // HDMI / DisplayPort monitor
    [InlineData("SWD", 0u, true)]       // network device
    [InlineData(null, null, false)]
    public void ClassifiesAudioEndpoints(string? enumerator, uint? formFactor, bool external) =>
        Assert.Equal(external, WindowsDecoding.IsExternalAudioEndpoint(enumerator, formFactor));

    [Fact]
    public void DecodesSsids()
    {
        Assert.Equal("Café-5G", WindowsDecoding.DecodeSsid("Café-5G"u8));
        Assert.Null(WindowsDecoding.DecodeSsid([]));
        Assert.Equal(new string('a', 32), WindowsDecoding.DecodeSsid(new byte[40].Select(_ => (byte)'a').ToArray()));
    }

    [Fact]
    public void InstanceMessagesRoundTrip()
    {
        Assert.Equal(["--settings"], InstanceMessage.Decode(InstanceMessage.Encode([])));
        Assert.Equal(["keepingyouawake:///activate?minutes=5", "--x y"],
            InstanceMessage.Decode(InstanceMessage.Encode(["keepingyouawake:///activate?minutes=5", "--x\ny"])));
        var huge = InstanceMessage.Encode([new string('x', 20_000)]);
        Assert.Equal(InstanceMessage.MaximumBytes, huge.Length);
        Assert.Single(InstanceMessage.Decode(new byte[20_000].Select(_ => (byte)'y').ToArray()));
    }

    [Fact]
    public void ShellCommandsQuoteThePath()
    {
        const string exe = @"C:\Program Files\KYA\KeepingYouAwake.exe";
        Assert.Equal("\"C:\\Program Files\\KYA\\KeepingYouAwake.exe\"", ShellCommands.StartAtLogin(exe));
        Assert.Equal("\"C:\\Program Files\\KYA\\KeepingYouAwake.exe\" \"%1\"", ShellCommands.OpenUrl(exe));
        Assert.Equal("\"C:\\Program Files\\KYA\\KeepingYouAwake.exe\",0", ShellCommands.Icon(exe));
    }
}

[Collection(StringsCollection.Name)]
public class TrayPresenterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<ActivationDuration> Durations =
        [ActivationDuration.Indefinite, ActivationDuration.FromMinutes(5), ActivationDuration.FromMinutes(60)];

    public TrayPresenterTests() => Strings.OverrideLanguage("en");
    public void Dispose() => Strings.OverrideLanguage(null);

    private static TrayState Inactive => new(false, null, null, null, Durations, ActivationDuration.FromMinutes(5), false);

    private static TrayState ActiveFor(TimeSpan remaining, ActivationSource source = ActivationSource.User, bool hidden = false) =>
        new(true, Now + remaining, source, ActivationDuration.FromMinutes(60), Durations, ActivationDuration.FromMinutes(5), hidden);

    [Fact]
    public void TooltipShowsStateAndCountdown()
    {
        Assert.Equal("KeepingYouAwake — inactive", TrayPresenter.Tooltip(Inactive, Now));
        Assert.Equal("KeepingYouAwake — 4:05 left", TrayPresenter.Tooltip(ActiveFor(TimeSpan.FromSeconds(245)), Now));
        Assert.Equal("KeepingYouAwake — preventing sleep", TrayPresenter.Tooltip(ActiveFor(TimeSpan.FromMinutes(1), hidden: true), Now));
        var indefinite = Inactive with { IsActive = true };
        Assert.Equal("KeepingYouAwake — preventing sleep", TrayPresenter.Tooltip(indefinite, Now));
        Assert.False(indefinite.ShowsCountdown);
        Assert.True(ActiveFor(TimeSpan.FromMinutes(1)).ShowsCountdown);
    }

    [Fact]
    public void TooltipIsCappedForTheShell()
    {
        Strings.OverrideLanguage("ja");
        var text = TrayPresenter.Tooltip(ActiveFor(TimeSpan.FromHours(3)), Now);
        Assert.True(text.Length <= TrayPresenter.MaximumTooltipLength);
        Assert.Contains("3:00:00", text);
    }

    [Fact]
    public void NotificationTexts()
    {
        Assert.Equal("Allowing Sleep", TrayPresenter.Notification(new SessionNotification(false, null), Now).Title);
        Assert.Equal("Preventing Sleep", TrayPresenter.Notification(new SessionNotification(true, null), Now).Title);
        var timed = TrayPresenter.Notification(new SessionNotification(true, Now.AddMinutes(90)), Now);
        Assert.Equal("Preventing Sleep for 1 hour, 30 minutes", timed.Title);
        Assert.Contains("go to sleep again", timed.Body);
        Assert.Equal("Preventing Sleep for 0 seconds",
            TrayPresenter.Notification(new SessionNotification(true, Now.AddMinutes(-1)), Now).Title);
    }

    [Fact]
    public void InactiveMenuOffersTheDefaultDuration()
    {
        var menu = TrayPresenter.BuildMenu(Inactive, Now);
        Assert.Equal("Activate (5 minutes)", menu[0].Title);
        Assert.Equal(TrayMenuAction.Toggle, menu[0].Action);
        Assert.Equal(
            [TrayMenuAction.Toggle, TrayMenuAction.None, TrayMenuAction.None, TrayMenuAction.None, TrayMenuAction.None,
             TrayMenuAction.ShowSettings, TrayMenuAction.ShowActivityLog, TrayMenuAction.None, TrayMenuAction.Quit],
            menu.Select(i => i.Action));
        Assert.Equal(3, menu.Count(i => i.IsSeparator));
    }

    [Fact]
    public void ActiveMenuShowsRemainingTimeSourceAndDeactivate()
    {
        var menu = TrayPresenter.BuildMenu(ActiveFor(TimeSpan.FromMinutes(10), ActivationSource.WatchedApp), Now);
        Assert.Equal("Remaining: 10:00", menu[0].Title);
        Assert.False(menu[0].Enabled);
        Assert.Equal("Started by: Watched application", menu[1].Title);
        Assert.Equal(TrayMenuAction.Deactivate, menu[2].Action);

        var userMenu = TrayPresenter.BuildMenu(ActiveFor(TimeSpan.FromMinutes(10)), Now);
        Assert.DoesNotContain(userMenu, i => i.Title.StartsWith("Started by", StringComparison.Ordinal));
    }

    [Fact]
    public void DurationSubmenusMarkDefaultAndCurrent()
    {
        var menu = TrayPresenter.BuildMenu(ActiveFor(TimeSpan.FromMinutes(10)), Now);
        var activateFor = menu.Single(i => i.Title == "Activate for Duration").Children!;
        Assert.Equal(["Indefinitely", "5 minutes (Default)", "1 hour", "-", "Until end of day"], activateFor.Select(i => i.Title));
        Assert.True(activateFor.Single(i => i.Duration == ActivationDuration.FromMinutes(60)).Checked);
        Assert.Equal(TrayMenuAction.ActivateUntilEndOfDay, activateFor[^1].Action);

        var defaults = menu.Single(i => i.Title == "Default Duration").Children!;
        Assert.All(defaults, i => Assert.Equal(TrayMenuAction.SetDefaultDuration, i.Action));
        Assert.Equal([false, true, false], defaults.Select(i => i.Checked));
    }

    [Theory]
    [InlineData(23, 59, 59.4, 1)]
    [InlineData(0, 0, 0, 86400)]
    [InlineData(12, 0, 0, 43200)]
    public void UntilNextMidnightNeverReturnsZero(int hour, int minute, double second, double expected)
    {
        var now = new DateTime(2026, 10, 21).AddHours(hour).AddMinutes(minute).AddSeconds(second);
        Assert.Equal(TimeSpan.FromSeconds(expected), TrayPresenter.UntilNextMidnight(now));
    }

    [Fact]
    public void DescribesScheduleWindows()
    {
        var window = new ScheduleWindow { Weekdays = [DayOfWeek.Monday, DayOfWeek.Friday], StartMinutes = 9 * 60 + 5, EndMinutes = 17 * 60 };
        Assert.Equal("Mon, Fri  09:05–17:00", TrayPresenter.DescribeScheduleWindow(window));
        Strings.OverrideLanguage("ja");
        Assert.Equal("月・金  09:05–17:00", TrayPresenter.DescribeScheduleWindow(window));
    }

    [Fact]
    public void TrayStateIsCapturedFromTheController()
    {
        using var h = new ControllerHarness(s => s.CountdownHidden = true);
        h.Controller.Start();
        h.Controller.Activate(TimeSpan.FromMinutes(5));
        var state = TrayState.From(h.Controller, h.Store.Settings);
        Assert.True(state.IsActive);
        Assert.Equal(ActivationSource.User, state.Source);
        Assert.Equal(ActivationDuration.FromMinutes(5), state.CurrentDuration);
        Assert.True(state.CountdownHidden);
        Assert.False(state.ShowsCountdown);
    }
}

public class UpdateCheckerApiTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task QueriesTheReleasesApiWithGitHubHeaders()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """[{"tag_name":"v2.0.0","html_url":"https://x/2"}]""");
        using var http = new HttpClient(handler);
        var release = await new UpdateChecker(http, "owner/repo").FindUpdateAsync("1.0.0", includePreReleases: false);

        Assert.Equal("v2.0.0", release!.Tag);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases?per_page=30", handler.Request!.RequestUri!.ToString());
        Assert.Contains("application/vnd.github+json", handler.Request.Headers.Accept.ToString());
        Assert.Equal("KeepingYouAwake/1.0.0", handler.Request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task HttpErrorsSurfaceAsExceptions()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Forbidden, "rate limited"));
        await Assert.ThrowsAsync<HttpRequestException>(() => new UpdateChecker(http).FindUpdateAsync("1.0.0", false));
    }

    [Fact]
    public async Task UnparsableCurrentVersionStillSendsAValidUserAgent()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        using var http = new HttpClient(handler);
        Assert.Null(await new UpdateChecker(http).FindUpdateAsync("dev-build", false));
        Assert.Equal("KeepingYouAwake/0", handler.Request!.Headers.UserAgent.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""[1, "x", {"tag_name": 5}, {"tag_name":"v9.0.0","draft":true}, {"tag_name":"latest"}]""")]
    public void IgnoresMalformedResponses(string json) => Assert.Null(UpdateChecker.SelectUpdate(json, "1.0.0", true));

    [Fact]
    public void ReleaseWithoutUrlOrPrereleaseFlag()
    {
        var update = UpdateChecker.SelectUpdate("""[{"tag_name":"V1.5"}]""", "1.0.0", false)!;
        Assert.Equal(new Version(1, 5, 0, 0), update.Version);
        Assert.Equal(string.Empty, update.PageUrl);
        Assert.False(update.IsPreRelease);
    }

    [Fact]
    public void LabelledTagsCountAsPreReleasesEvenWithoutTheFlag() =>
        Assert.Null(UpdateChecker.SelectUpdate("""[{"tag_name":"v3.0.0-rc.1","prerelease":false}]""", "1.0.0", false));

    [Theory]
    [InlineData("v1.2.3+build.5", "1.2.3.0", null)]
    [InlineData("2", "2.0.0.0", null)]
    [InlineData("1.0.0-", "1.0.0.0", null)]
    [InlineData("1.0.0-beta.2", "1.0.0.0", "beta.2")]
    public void ParsesVersionVariants(string text, string version, string? label)
    {
        var parsed = UpdateChecker.ParseVersion(text)!.Value;
        Assert.Equal(Version.Parse(version), parsed.Version);
        Assert.Equal(label, parsed.Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vnext")]
    [InlineData("1.x")]
    public void RejectsNonVersions(string text) => Assert.Null(UpdateChecker.ParseVersion(text));

    [Fact]
    public void LongerPreReleaseLabelsSortAfterTheirPrefix()
    {
        var a = UpdateChecker.ParseVersion("1.0.0-beta")!.Value;
        var b = UpdateChecker.ParseVersion("1.0.0-beta.1")!.Value;
        Assert.True(UpdateChecker.Compare(b.Version, b.Label, a.Version, a.Label) > 0);
        var numeric = UpdateChecker.ParseVersion("1.0.0-1")!.Value;
        Assert.True(UpdateChecker.Compare(numeric.Version, numeric.Label, a.Version, a.Label) < 0);
    }
}

public class SmallHelperTests
{
    [Fact]
    public void WatchedItemListHelpers()
    {
        Assert.Equal(["a", "B"], WatchedItems.WithItemAdded(["a"], " B "));
        Assert.Equal(["a"], WatchedItems.WithItemAdded(["a"], "A"));
        Assert.Equal(["a"], WatchedItems.WithItemAdded(["a"], "   "));
        Assert.Equal(["b"], WatchedItems.WithItemRemoved(["A", "b"], "a"));
        Assert.Equal(["x", "Item"], WatchedItems.WithItemEnabled(["x", "ITEM"], "Item", enabled: false));
        Assert.Equal(["x"], WatchedItems.WithItemEnabled(["x", "ITEM"], "item", enabled: true));
        Assert.True(WatchedItems.AnyJoined(["Office"], ["", "office"]));
        Assert.False(WatchedItems.AnyJoined(["Office"], []));
    }

    [Fact]
    public void DataDirectoryOverride()
    {
        var appData = Path.Combine(Path.GetTempPath(), "appdata");
        Assert.Equal(Path.Combine(appData, "KeepingYouAwake"), AppPaths.ResolveDataDirectory(null, appData));
        Assert.Equal(Path.Combine(appData, "KeepingYouAwake"), AppPaths.ResolveDataDirectory("  ", appData));
        var custom = Path.Combine(Path.GetTempPath(), "kya-data");
        Assert.Equal(Path.GetFullPath(custom), AppPaths.ResolveDataDirectory($" {custom} ", appData));
        Assert.EndsWith("KeepingYouAwake", AppPaths.DataDirectory);
        Assert.EndsWith("settings.json", SettingsStore.DefaultFilePath);
        Assert.EndsWith("activity.jsonl", ActivityLogger.DefaultFilePath);
    }

    [Fact]
    public void QuitCommand()
    {
        Assert.Equal(AppCommandKind.Quit, AppCommand.ParseUrl("keepingyouawake:///quit")!.Kind);
        Assert.Equal(AppCommandKind.Quit, AppCommand.ParseArguments(["--quit"])!.Kind);
        Assert.Equal(AppCommandKind.ShowSettings, AppCommand.ParseUrl("keepingyouawake://settings")!.Kind);
        Assert.Equal(AppCommandKind.ShowSettings, AppCommand.ParseArguments(["/settings"])!.Kind);
        Assert.Null(AppCommand.ParseArguments(["--activate", "nope"]));
        Assert.Null(AppCommand.ParseUrl("keepingyouawake:///activate?=1&minutes")); // empty value is invalid
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData(0.0, true, null)]
    [InlineData(60.0, true, 60.0)]
    [InlineData(-1.0, false, -1.0)]
    [InlineData(1e9, false, 1e9)]
    public void IntervalNormalization(double? seconds, bool valid, double? normalized)
    {
        var ok = AppController.TryNormalizeInterval(seconds is { } s ? TimeSpan.FromSeconds(s) : null, out var result);
        Assert.Equal(valid, ok);
        Assert.Equal(normalized, result?.TotalSeconds);
    }

    [Theory]
    [InlineData(0ul, 0ul, 10ul, 100ul, 90.0)]
    [InlineData(0ul, 0ul, 100ul, 100ul, 0.0)]
    [InlineData(10ul, 100ul, 10ul, 100ul, null)]   // no progress
    [InlineData(50ul, 100ul, 40ul, 200ul, null)]   // idle went backwards
    [InlineData(0ul, 0ul, 500ul, 100ul, 0.0)]      // idle > total is clamped
    public void CpuBusyPercent(ulong idle0, ulong total0, ulong idle1, ulong total1, double? expected) =>
        Assert.Equal(expected, CpuLoadMonitor.BusyPercent(new CpuTimes(idle0, total0), new CpuTimes(idle1, total1)));

    [Fact]
    public void ActivityLogEntryRecordEquality()
    {
        var at = DateTimeOffset.UnixEpoch;
        Assert.Equal(new ActivityLogEntry(at, null, "user", -1, null), new ActivityLogEntry(at, null, "user", -1, null));
    }
}

public class WlanParsingTests
{
    private static byte[] InterfaceList(params (Guid Id, int State)[] interfaces)
    {
        var buffer = new byte[WindowsDecoding.WlanInterfaceListHeaderSize + interfaces.Length * WindowsDecoding.WlanInterfaceInfoSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), interfaces.Length);
        for (var i = 0; i < interfaces.Length; i++)
        {
            var info = buffer.AsSpan(WindowsDecoding.WlanInterfaceListHeaderSize + i * WindowsDecoding.WlanInterfaceInfoSize);
            interfaces[i].Id.TryWriteBytes(info);
            BitConverter.TryWriteBytes(info.Slice(16 + 512, 4), interfaces[i].State);
        }
        return buffer;
    }

    private static byte[] ConnectionAttributes(string ssid, int? lengthOverride = null)
    {
        var buffer = new byte[WindowsDecoding.WlanConnectionSsidOffset + 4 + 32 + 64];
        var bytes = System.Text.Encoding.UTF8.GetBytes(ssid);
        BitConverter.TryWriteBytes(buffer.AsSpan(WindowsDecoding.WlanConnectionSsidOffset, 4), lengthOverride ?? bytes.Length);
        bytes.CopyTo(buffer, WindowsDecoding.WlanConnectionSsidOffset + 4);
        return buffer;
    }

    [Fact]
    public void ParsesInterfacesAndTheirState()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var parsed = WindowsDecoding.ParseWlanInterfaceList(InterfaceList((a, 1), (b, 4)));
        Assert.Equal([(a, true), (b, false)], parsed);
    }

    [Fact]
    public void TruncatedOrEmptyListsAreSafe()
    {
        Assert.Empty(WindowsDecoding.ParseWlanInterfaceList([]));
        var list = InterfaceList((Guid.NewGuid(), 1), (Guid.NewGuid(), 1));
        Assert.Single(WindowsDecoding.ParseWlanInterfaceList(list.AsSpan(0, list.Length - 10))); // count says 2, room for 1
    }

    [Fact]
    public void ParsesTheConnectedSsid()
    {
        Assert.Equal("Office-5G", WindowsDecoding.ParseConnectionSsid(ConnectionAttributes("Office-5G")));
        Assert.Null(WindowsDecoding.ParseConnectionSsid(ConnectionAttributes(""))); // redacted
        Assert.Equal("ab", WindowsDecoding.ParseConnectionSsid(ConnectionAttributes("abc", lengthOverride: 2)));
        Assert.Equal(32, WindowsDecoding.ParseConnectionSsid(ConnectionAttributes(new string('x', 32), lengthOverride: 99))!.Length);
        Assert.Null(WindowsDecoding.ParseConnectionSsid(new byte[100])); // too short
    }
}
