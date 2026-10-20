using System.Globalization;

namespace KeepingYouAwake.Core;

public enum AppCommandKind
{
    Activate,
    Deactivate,
    Toggle,
    ShowSettings,
}

/// <summary>
/// A command from the <c>keepingyouawake:</c> URL scheme or the command line:
/// <c>keepingyouawake:///activate?minutes=30</c>, <c>keepingyouawake:///deactivate</c>,
/// <c>keepingyouawake:///toggle</c>.
/// </summary>
public sealed record AppCommand(AppCommandKind Kind, TimeSpan? Duration = null, bool UseDefaultDuration = false)
{
    public const string Scheme = "keepingyouawake";

    /// <summary>
    /// Parses a URL. Accepts <c>keepingyouawake:///activate</c> and <c>keepingyouawake://activate</c>.
    /// For activate: <c>seconds</c>, <c>minutes</c> or <c>hours</c> (first present wins, rounded up);
    /// no parameter means the default duration; an unparsable or negative value yields null.
    /// </summary>
    public static AppCommand? ParseUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) { return null; }
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) { return null; }

        var action = (uri.Host + uri.AbsolutePath).Trim('/').ToLowerInvariant();
        var query = ParseQuery(uri.Query);

        return action switch
        {
            "activate" => ParseActivate(query),
            "deactivate" => new AppCommand(AppCommandKind.Deactivate),
            "toggle" => new AppCommand(AppCommandKind.Toggle),
            "settings" or "preferences" => new AppCommand(AppCommandKind.ShowSettings),
            _ => null,
        };
    }

    /// <summary>Parses process arguments: a URL, or <c>--activate [seconds]</c> / <c>--deactivate</c> / <c>--toggle</c>.</summary>
    public static AppCommand? ParseArguments(IReadOnlyList<string> args)
    {
        if (args.Count == 0) { return null; }
        var first = args[0];
        if (first.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) { return ParseUrl(first); }

        return first.ToLowerInvariant() switch
        {
            "--activate" or "/activate" when args.Count > 1 =>
                ParseSeconds(args[1], 1) is { } d ? new AppCommand(AppCommandKind.Activate, d) : null,
            "--activate" or "/activate" => new AppCommand(AppCommandKind.Activate, UseDefaultDuration: true),
            "--deactivate" or "/deactivate" => new AppCommand(AppCommandKind.Deactivate),
            "--toggle" or "/toggle" => new AppCommand(AppCommandKind.Toggle),
            "--settings" or "/settings" => new AppCommand(AppCommandKind.ShowSettings),
            _ => null,
        };
    }

    private static AppCommand? ParseActivate(Dictionary<string, string> query)
    {
        (string Key, double Unit)[] units = [("seconds", 1), ("minutes", 60), ("hours", 3600)];
        foreach (var (key, unit) in units)
        {
            if (!query.TryGetValue(key, out var value)) { continue; }
            return ParseSeconds(value, unit) is { } duration ? new AppCommand(AppCommandKind.Activate, duration) : null;
        }
        return new AppCommand(AppCommandKind.Activate, UseDefaultDuration: true);
    }

    /// <summary>A value in <paramref name="unit"/> seconds, rounded up. 0 means indefinite (null duration).</summary>
    private static TimeSpan? ParseSeconds(string value, double unit)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) { return null; }
        if (!double.IsFinite(number) || number < 0) { return null; }
        var seconds = Math.Ceiling(number) * unit;
        if (seconds > ActivationDuration.MaximumSeconds) { return null; }
        return TimeSpan.FromSeconds(seconds);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            if (key.Length == 0 || result.ContainsKey(key)) { continue; }
            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
        }
        return result;
    }
}
