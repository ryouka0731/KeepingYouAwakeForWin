using System.Globalization;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core.Tests;

/// <summary>
/// Property-based tests: invariants checked against hundreds of generated inputs
/// instead of hand-picked examples. FsCheck shrinks any failure to a minimal case.
/// </summary>
public class PropertyTests
{
    private const int Week = 7 * ScheduleWindow.MinutesPerDay;

    private static Arbitrary<ScheduleWindow> Windows =>
        (from days in Gen.SubListOf(Enum.GetValues<DayOfWeek>())
         from start in Gen.Choose(0, ScheduleWindow.MinutesPerDay - 1)
         from end in Gen.Choose(0, ScheduleWindow.MinutesPerDay - 1)
         select new ScheduleWindow { Weekdays = days.ToList(), StartMinutes = start, EndMinutes = end }).ToArbitrary();

    private static Arbitrary<int> MinuteOfWeek => Gen.Choose(0, Week - 1).ToArbitrary();

    /// <summary>
    /// An independent oracle: the window covers, from each selected day's start, a run of
    /// minutes of its length (wrapping past midnight and past the end of the week).
    /// </summary>
    private static bool Covered(ScheduleWindow w, int minuteOfWeek)
    {
        var length = w.EndMinutes > w.StartMinutes
            ? w.EndMinutes - w.StartMinutes
            : ScheduleWindow.MinutesPerDay - w.StartMinutes + w.EndMinutes;
        return w.Weekdays.Any(day =>
        {
            var from = (int)day * ScheduleWindow.MinutesPerDay + w.StartMinutes;
            var offset = ((minuteOfWeek - from) % Week + Week) % Week;
            return offset < length;
        });
    }

    [Property(MaxTest = 1000)]
    public Property ScheduleWindowMatchesTheMinuteRunOracle() =>
        Prop.ForAll(Windows, MinuteOfWeek, (window, minute) =>
        {
            var sunday = new DateTime(2026, 10, 18); // a Sunday
            var time = sunday.AddMinutes(minute);
            return window.Contains(time) == Covered(window, minute);
        });

    [Property]
    public Property NormalizedWindowsStayInRangeAndKeepMembership() =>
        Prop.ForAll(Windows, MinuteOfWeek, (window, minute) =>
        {
            var normalized = window.Normalized();
            var time = new DateTime(2026, 10, 18).AddMinutes(minute);
            return normalized.StartMinutes is >= 0 and < ScheduleWindow.MinutesPerDay
                && normalized.Weekdays.Distinct().Count() == normalized.Weekdays.Count
                && normalized.Contains(time) == window.Contains(time);
        });

    private static Arbitrary<string> Versions =>
        (from major in Gen.Choose(0, 5)
         from minor in Gen.Choose(0, 5)
         from patch in Gen.Choose(0, 5)
         from label in Gen.Elements<string?>(null, "alpha", "alpha.1", "beta", "beta.2", "beta.10", "rc.1", "1", "2")
         select label is null ? $"{major}.{minor}.{patch}" : $"{major}.{minor}.{patch}-{label}").ToArbitrary();

    private static int Compare(string a, string b)
    {
        var pa = UpdateChecker.ParseVersion(a)!.Value;
        var pb = UpdateChecker.ParseVersion(b)!.Value;
        return Math.Sign(UpdateChecker.Compare(pa.Version, pa.Label, pb.Version, pb.Label));
    }

    [Property(MaxTest = 500)]
    public Property VersionOrderIsATotalOrder() =>
        Prop.ForAll(Versions, Versions, Versions, (a, b, c) =>
            Compare(a, a) == 0
            && Compare(a, b) == -Compare(b, a)
            && (Compare(a, b) > 0 && Compare(b, c) > 0 ? Compare(a, c) > 0 : true));

    [Property]
    public Property AReleaseOutranksItsPreReleases() =>
        Prop.ForAll(Versions, v =>
        {
            var release = v.Split('-')[0];
            return v == release || Compare(release, v) > 0;
        });

    [Property]
    public Property VersionsParseBackWithOrWithoutTheVPrefix() =>
        Prop.ForAll(Versions, v =>
            UpdateChecker.ParseVersion(v) == UpdateChecker.ParseVersion("v" + v)
            && UpdateChecker.ParseVersion(v + "+build.7") == UpdateChecker.ParseVersion(v));

    [Property(MaxTest = 500)]
    public Property ActivateUrlsRoundTripTheirDuration() =>
        Prop.ForAll(Gen.Choose(0, (int)ActivationDuration.MaximumSeconds).ToArbitrary(), seconds =>
        {
            var command = AppCommand.ParseUrl($"keepingyouawake:///activate?seconds={seconds}");
            return command is { Kind: AppCommandKind.Activate } && command.Duration == TimeSpan.FromSeconds(seconds);
        });

    [Property]
    public Property FractionalAndUnitDurationsRoundUpToWholeUnits() =>
        Prop.ForAll(Gen.Choose(0, 5000).ToArbitrary(), Gen.Elements(("minutes", 60), ("hours", 3600)).ToArbitrary(),
            (tenths, unit) =>
            {
                var value = (tenths / 10.0).ToString(CultureInfo.InvariantCulture);
                var command = AppCommand.ParseUrl($"keepingyouawake:///activate?{unit.Item1}={value}");
                var expected = Math.Ceiling(tenths / 10.0) * unit.Item2;
                return expected > ActivationDuration.MaximumSeconds
                    ? command is null
                    : command?.Duration == TimeSpan.FromSeconds(expected);
            });

    [Property]
    public Property NegativeOrTooLongDurationsAreRejected() =>
        Prop.ForAll(Gen.OneOf(Gen.Choose(int.MinValue, -1), Gen.Choose((int)ActivationDuration.MaximumSeconds + 1, int.MaxValue)).ToArbitrary(),
            seconds => AppCommand.ParseUrl($"keepingyouawake:///activate?seconds={seconds}") is null);

    [Property]
    public Property CountdownParsesBackToTheRoundedUpSeconds() =>
        Prop.ForAll(Gen.Choose(0, 400_000).ToArbitrary(), Gen.Choose(0, 999).ToArbitrary(), (seconds, millis) =>
        {
            var span = TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(millis);
            var parts = Strings.Countdown(span).Split(':').Select(int.Parse).ToArray();
            var total = parts.Length == 3 ? parts[0] * 3600 + parts[1] * 60 + parts[2] : parts[0] * 60 + parts[1];
            var minutesInRange = parts.Length != 3 || parts[1] < 60;
            return total == (long)Math.Ceiling(span.TotalSeconds) && parts[^1] < 60 && minutesInRange;
        });

    private static Arbitrary<string> Names =>
        Gen.Elements("a", "A", "obs64.exe", "OBS64.EXE", "Office", "office", "Lab", "x y").ToArbitrary();

    private static Arbitrary<string[]> NameLists => Gen.ArrayOf(Gen.Elements("a", "A", "Office", "office", "Lab", "x y")).ToArbitrary();

    [Property]
    public Property WatchedListEditsBehaveLikeCaseInsensitiveSets() =>
        Prop.ForAll(NameLists, Names, (items, item) =>
        {
            var added = WatchedItems.WithItemAdded(items, item);
            var addedTwice = WatchedItems.WithItemAdded(added, item);
            var removed = WatchedItems.WithItemRemoved(added, item);
            var off = WatchedItems.WithItemEnabled([], item, enabled: false);
            var backOn = WatchedItems.WithItemEnabled(off, item.ToUpperInvariant(), enabled: true);
            return addedTwice.SequenceEqual(added)
                && added.Contains(item, StringComparer.OrdinalIgnoreCase)
                && !removed.Contains(item, StringComparer.OrdinalIgnoreCase)
                && !WatchedItems.Enabled(added, off, ignoreCase: true).Contains(item, StringComparer.OrdinalIgnoreCase)
                && backOn.Count == 0;
        });

    [Property]
    public Property InstanceMessagesRoundTripArguments() =>
        Prop.ForAll(Gen.ArrayOf(Gen.Elements("--activate", "60", "keepingyouawake:///toggle", "--quit", "日本語", "a b")).ToArbitrary(), args =>
        {
            var decoded = InstanceMessage.Decode(InstanceMessage.Encode(args));
            return args.Length == 0 ? decoded.SequenceEqual(["--settings"]) : decoded.SequenceEqual(args);
        });

    [Property]
    public bool CpuBusyShareIsAPercentage(ulong idle, ulong total, uint dIdle, uint dTotal)
    {
        var before = new CpuTimes(idle / 4, total / 4);
        var after = new CpuTimes(before.Idle + dIdle, before.Total + dTotal);
        var busy = CpuLoadMonitor.BusyPercent(before, after);
        return busy is null ? dTotal == 0 : busy is >= 0 and <= 100;
    }

    [Property]
    public bool PowerStatusDecodingIsTotal(byte ac, byte flag, byte percent, byte status)
    {
        var decoded = WindowsDecoding.DecodePowerStatus(ac, flag, percent, status);
        return decoded.CapacityPercent is >= 0 and <= 100
            && (decoded.State != BatteryState.Unplugged || ac == 0)
            && decoded.BatterySaverEnabled == (status == 1);
    }

    [Property]
    public Property DurationTextIsNeverEmpty() =>
        Prop.ForAll(Gen.Choose(0, (int)ActivationDuration.MaximumSeconds).ToArbitrary(), seconds =>
            !string.IsNullOrWhiteSpace(Strings.DurationTitle(new ActivationDuration(seconds))));
}

/// <summary>
/// Model-based test: random sequences of user actions, trigger changes and elapsed time
/// against <see cref="AppController"/>, checking its invariants after every step.
/// </summary>
public class ControllerModelPropertyTests
{
    private enum Step
    {
        UserActivateTimed,
        UserActivateIndefinite,
        UserDeactivate,
        UserToggle,
        AppStarts,
        AppQuits,
        DownloadStarts,
        DownloadEnds,
        PlugIn,
        Unplug,
        DisplayConnected,
        DisplayDisconnected,
        TimePasses,
    }

    private static Arbitrary<Step[]> Scripts => Gen.ArrayOf(Gen.Elements(Enum.GetValues<Step>())).ToArbitrary();

    [Property(MaxTest = 300)]
    public Property InvariantsHoldForAnySequenceOfEvents() =>
        Prop.ForAll(Scripts, script =>
        {
            using var h = new ControllerHarness(s =>
            {
                s.WatchedApplications = ["app.exe"];
                s.ActivateOnACPower = true;
                s.ActivateOnExternalDisplay = true;
            });
            var downloads = Directory.CreateDirectory(Path.Join(h.Directory, "dl")).FullName;
            h.Store.Update(s =>
            {
                s.DownloadInProgressActivationEnabled = true;
                s.DownloadDirectories = [downloads];
            });
            h.Power.Set(BatteryState.Unplugged, 80);
            h.Controller.Start();

            foreach (var step in script)
            {
                var userSessionBefore = h.Controller.CurrentSource == ActivationSource.User;
                var userFireBefore = h.Controller.FireDate;
                var isUserAction = Apply(h, step);

                // 1. The OS request is held exactly while a session runs.
                if (h.Assertion.Held != h.Controller.IsActive) { return false; }
                // 2. A trigger never ends (or replaces) a session the user started, unless it expired.
                if (userSessionBefore && !isUserAction && h.Controller.CurrentSource != ActivationSource.User
                    && !(step == Step.TimePasses && userFireBefore is not null)) { return false; }
                // 3. The activity log has at most one open entry, and it's the running session.
                var open = h.Log.RecentEntries(1000).Count(e => e.EndedAt is null);
                if (open != (h.Controller.IsActive ? 1 : 0)) { return false; }
            }
            return true;
        });

    /// <summary>Applies a step; returns true for actions the user takes.</summary>
    private static bool Apply(ControllerHarness h, Step step)
    {
        switch (step)
        {
            case Step.UserActivateTimed: h.Controller.Activate(TimeSpan.FromMinutes(5)); return true;
            case Step.UserActivateIndefinite: h.Controller.Activate(null); return true;
            case Step.UserDeactivate: h.Controller.Deactivate(); return true;
            case Step.UserToggle: h.Controller.Toggle(); return true;
            case Step.AppStarts:
                h.Processes.Running.Add("app.exe");
                h.Scheduler.Advance(TimeSpan.FromSeconds(5));
                return false;
            case Step.AppQuits:
                h.Processes.Running.Clear();
                h.Scheduler.Advance(TimeSpan.FromSeconds(5));
                return false;
            case Step.DownloadStarts:
                File.WriteAllText(Path.Join(h.Directory, "dl", "file.part"), "x");
                h.Scheduler.Advance(TimeSpan.FromSeconds(10));
                return false;
            case Step.DownloadEnds:
                File.Delete(Path.Join(h.Directory, "dl", "file.part"));
                h.Scheduler.Advance(TimeSpan.FromSeconds(10));
                return false;
            case Step.PlugIn: h.Power.Set(BatteryState.Charging, 80); return false;
            case Step.Unplug: h.Power.Set(BatteryState.Unplugged, 80); return false;
            case Step.DisplayConnected: h.Displays.Set(1); return false;
            case Step.DisplayDisconnected: h.Displays.Set(0); return false;
            default:
                h.Scheduler.Advance(TimeSpan.FromMinutes(3));
                return false;
        }
    }
}
