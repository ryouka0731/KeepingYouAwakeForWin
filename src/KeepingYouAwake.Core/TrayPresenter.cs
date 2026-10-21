namespace KeepingYouAwake.Core;

public enum TrayMenuAction
{
    None,
    Toggle,
    Deactivate,
    ActivateDuration,
    ActivateUntilEndOfDay,
    SetDefaultDuration,
    ShowSettings,
    ShowActivityLog,
    Quit,
}

/// <summary>One entry of the tray menu, independent of the UI toolkit.</summary>
public sealed record TrayMenuItem(
    string Title,
    TrayMenuAction Action = TrayMenuAction.None,
    ActivationDuration? Duration = null,
    bool Enabled = true,
    bool Checked = false,
    IReadOnlyList<TrayMenuItem>? Children = null)
{
    public static readonly TrayMenuItem Separator = new("-") { IsSeparator = true };

    public bool IsSeparator { get; private init; }
}

/// <summary>What the tray shows, captured from <see cref="AppController"/> and the settings.</summary>
public sealed record TrayState(
    bool IsActive,
    DateTimeOffset? FireDate,
    ActivationSource? Source,
    ActivationDuration? CurrentDuration,
    IReadOnlyList<ActivationDuration> Durations,
    ActivationDuration DefaultDuration,
    bool CountdownHidden)
{
    public static TrayState From(AppController controller, AppSettings settings) => new(
        controller.IsActive, controller.FireDate, controller.CurrentSource, controller.CurrentDuration,
        controller.Durations.Durations, controller.Durations.Default, settings.CountdownHidden);

    /// <summary>Whether the tooltip needs a once-a-second countdown refresh.</summary>
    public bool ShowsCountdown => IsActive && FireDate is not null && !CountdownHidden;
}

/// <summary>Text and menu structure of the tray icon. Pure, so it is unit-tested without WinForms.</summary>
public static class TrayPresenter
{
    /// <summary>The notification-area tooltip; Windows truncates longer text, so it's capped here.</summary>
    public const int MaximumTooltipLength = 127;

    public static string Tooltip(TrayState state, DateTimeOffset now)
    {
        string text;
        if (!state.IsActive) { text = Strings.Get("TooltipInactive"); }
        else if (state.ShowsCountdown) { text = Strings.Format("TooltipActiveRemaining", Strings.Countdown(state.FireDate!.Value - now)); }
        else { text = Strings.Get("TooltipActive"); }
        return text.Length > MaximumTooltipLength ? text[..MaximumTooltipLength] : text;
    }

    public static (string Title, string Body) Notification(SessionNotification notification, DateTimeOffset now)
    {
        if (!notification.Activating)
        {
            return (Strings.Get("AllowingSleepTitle"), Strings.Get("AllowingSleepBody"));
        }
        if (notification.FireDate is { } fireDate)
        {
            var remaining = TimeSpan.FromSeconds(Math.Max(0, Math.Round((fireDate - now).TotalSeconds)));
            return (Strings.Format("PreventingSleepForTitle", Strings.DurationText(remaining)), Strings.Get("PreventingSleepForBody"));
        }
        return (Strings.Get("PreventingSleepTitle"), Strings.Get("PreventingSleepBody"));
    }

    /// <summary>Time until the next local midnight in whole seconds, rounded up so it is never 0 (= indefinite).</summary>
    public static TimeSpan UntilNextMidnight(DateTime localNow) =>
        TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling((localNow.Date.AddDays(1) - localNow).TotalSeconds)));

    public static IReadOnlyList<TrayMenuItem> BuildMenu(TrayState state, DateTimeOffset now)
    {
        var items = new List<TrayMenuItem>();
        items.AddRange(StatusItems(state, now));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Strings.Get("ActivateForDuration"), Children: DurationItems(state)));
        items.Add(new TrayMenuItem(Strings.Get("SetDefaultDuration"), Children: DefaultDurationItems(state)));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Strings.Get("SettingsEllipsis"), TrayMenuAction.ShowSettings));
        items.Add(new TrayMenuItem(Strings.Get("ShowActivityLogEllipsis"), TrayMenuAction.ShowActivityLog));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Strings.Get("Quit"), TrayMenuAction.Quit));
        return items;
    }

    private static IEnumerable<TrayMenuItem> StatusItems(TrayState state, DateTimeOffset now)
    {
        if (!state.IsActive)
        {
            var title = $"{Strings.Get("Activate")} ({Strings.DurationTitle(state.DefaultDuration)})";
            yield return new TrayMenuItem(title, TrayMenuAction.Toggle);
            yield break;
        }
        if (state.FireDate is { } fireDate)
        {
            yield return new TrayMenuItem(Strings.Format("RemainingFormat", Strings.Countdown(fireDate - now)), Enabled: false);
        }
        if (state.Source is { } source && source != ActivationSource.User)
        {
            yield return new TrayMenuItem(Strings.Format("StartedByFormat", Strings.SourceName(source.ToLogString())), Enabled: false);
        }
        yield return new TrayMenuItem(Strings.Get("Deactivate"), TrayMenuAction.Deactivate);
    }

    private static List<TrayMenuItem> DurationItems(TrayState state)
    {
        var items = state.Durations.Select(d => new TrayMenuItem(
            d == state.DefaultDuration ? $"{Strings.DurationTitle(d)} {Strings.Get("IsDefaultSuffix")}" : Strings.DurationTitle(d),
            TrayMenuAction.ActivateDuration, d, Checked: state.CurrentDuration == d)).ToList();
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem(Strings.Get("UntilEndOfDay"), TrayMenuAction.ActivateUntilEndOfDay));
        return items;
    }

    private static List<TrayMenuItem> DefaultDurationItems(TrayState state) =>
        state.Durations.Select(d => new TrayMenuItem(Strings.DurationTitle(d), TrayMenuAction.SetDefaultDuration, d,
            Checked: d == state.DefaultDuration)).ToList();

    /// <summary>"Mon, Tue  09:00–17:00" / "月・火  09:00–17:00".</summary>
    public static string DescribeScheduleWindow(ScheduleWindow window)
    {
        var days = string.Join(Strings.Get("List.separator"), window.Weekdays.Select(Strings.WeekdayName));
        static string Time(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
        return $"{days}  {Time(window.StartMinutes)}–{Time(window.EndMinutes)}";
    }
}
