namespace KeepingYouAwake.Core;

/// <summary>A selectable session length. <c>0</c> seconds means indefinite.</summary>
public readonly record struct ActivationDuration(long Seconds) : IComparable<ActivationDuration>
{
    public static readonly ActivationDuration Indefinite = new(0);

    /// <summary>Longest accepted session (one year); longer requests are rejected, not clamped.</summary>
    public const long MaximumSeconds = 366L * 24 * 3600;

    public static IReadOnlyList<ActivationDuration> Defaults { get; } =
    [
        FromMinutes(5), FromMinutes(10), FromMinutes(15), FromMinutes(30),
        FromMinutes(60), FromMinutes(120), FromMinutes(300),
    ];

    public bool IsIndefinite => Seconds == 0;

    public TimeSpan? TimeSpan => IsIndefinite ? null : System.TimeSpan.FromSeconds(Seconds);

    public static ActivationDuration FromMinutes(long minutes) => new(minutes * 60);

    /// <summary>
    /// Builds a duration from the Add Duration dialog's fields. Returns null when
    /// minutes or seconds exceed 59 or the total is zero.
    /// </summary>
    public static ActivationDuration? FromComponents(int hours, int minutes, int seconds)
    {
        if (hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or > 59) { return null; }
        long total = hours * 3600L + minutes * 60L + seconds;
        return total == 0 || total > MaximumSeconds ? null : new ActivationDuration(total);
    }

    public int CompareTo(ActivationDuration other) => Seconds.CompareTo(other.Seconds);
}

/// <summary>
/// The user's list of durations (Activate for Duration menu) and the default one used
/// by a plain click on the tray icon. Backed by <see cref="AppSettings"/>.
/// </summary>
public sealed class ActivationDurationsController
{
    private readonly SettingsStore store;

    public ActivationDurationsController(SettingsStore store)
    {
        this.store = store;
        if (Stored().Count == 0)
        {
            store.Update(s => s.Durations = ActivationDuration.Defaults.Select(d => d.Seconds).ToList());
        }
    }

    /// <summary>Indefinite plus the stored durations, ascending.</summary>
    public IReadOnlyList<ActivationDuration> Durations =>
        Stored().Prepend(ActivationDuration.Indefinite).Distinct().Order().ToList();

    public ActivationDuration Default
    {
        get
        {
            var seconds = store.Settings.DefaultDurationSeconds;
            return seconds > 0 && Stored().Contains(new ActivationDuration(seconds))
                ? new ActivationDuration(seconds)
                : ActivationDuration.Indefinite;
        }
        set
        {
            if (!Durations.Contains(value)) { return; }
            store.Update(s => s.DefaultDurationSeconds = value.Seconds);
        }
    }

    public bool Add(ActivationDuration duration)
    {
        if (duration.IsIndefinite || Stored().Contains(duration)) { return false; }
        store.Update(s => s.Durations = [.. s.Durations, duration.Seconds]);
        return true;
    }

    public bool Remove(ActivationDuration duration)
    {
        if (duration.IsIndefinite || !Stored().Contains(duration)) { return false; }
        store.Update(s =>
        {
            s.Durations = s.Durations.Where(x => x != duration.Seconds).ToList();
            // The default falls back to indefinite when it gets removed.
            if (s.DefaultDurationSeconds == duration.Seconds) { s.DefaultDurationSeconds = 0; }
        });
        return true;
    }

    public void Reset()
    {
        store.Update(s =>
        {
            s.Durations = ActivationDuration.Defaults.Select(d => d.Seconds).ToList();
            if (!s.Durations.Contains(s.DefaultDurationSeconds)) { s.DefaultDurationSeconds = 0; }
        });
    }

    private List<ActivationDuration> Stored() =>
        store.Settings.Durations.Where(x => x is > 0 and <= ActivationDuration.MaximumSeconds).Select(x => new ActivationDuration(x)).ToList();
}
