using System.Globalization;

namespace KeepingYouAwake.Core;

/// <summary>
/// User-facing text, loaded from the embedded <c>Localization/&lt;language&gt;.json</c> tables
/// and chosen by <see cref="CultureInfo.CurrentUICulture"/>. English is the reference:
/// a key missing from a language falls back to it. Add a language by adding its JSON file.
/// </summary>
public static class Strings
{
    public const string FallbackLanguage = "en";

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Tables = new(LoadTables);
    private static string? languageOverride;

    /// <summary>Codes of the bundled languages, e.g. <c>en</c>, <c>ja</c>, <c>zh-Hant</c>.</summary>
    public static IReadOnlyList<string> Languages => Tables.Value.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Forces a language (tests). Null follows the UI culture.</summary>
    public static void OverrideLanguage(string? language) => languageOverride = language;

    /// <summary>The bundled language used for the current UI culture.</summary>
    public static string Language => languageOverride is { } forced && Tables.Value.ContainsKey(forced)
        ? forced
        : ResolveLanguage(CultureInfo.CurrentUICulture);

    public static bool IsJapanese => Language == "ja";

    /// <summary>
    /// Maps a culture to a bundled language: Traditional Chinese (zh-Hant, zh-TW, zh-HK, zh-MO) and
    /// other Chinese (zh) are told apart; everything else goes by its two-letter code; unknown → English.
    /// </summary>
    public static string ResolveLanguage(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (c.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return Available(IsTraditionalChinese(c.Name) ? "zh-Hant" : "zh");
            }
            if (Tables.Value.ContainsKey(c.Name)) { return c.Name; }
            if (Tables.Value.ContainsKey(c.TwoLetterISOLanguageName)) { return c.TwoLetterISOLanguageName; }
        }
        return FallbackLanguage;
    }

    private static string Available(string code) => Tables.Value.ContainsKey(code) ? code : FallbackLanguage;

    private static readonly string[] TraditionalChineseMarkers = ["Hant", "-TW", "-HK", "-MO"];

    private static bool IsTraditionalChinese(string cultureName) =>
        TraditionalChineseMarkers.Any(marker => cultureName.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static string Get(string key) => Get(key, Language);

    /// <summary>The text of <paramref name="key"/> in <paramref name="language"/>, falling back to English, then to the key.</summary>
    public static string Get(string key, string language)
    {
        var tables = Tables.Value;
        if (tables.TryGetValue(language, out var table) && table.TryGetValue(key, out var text)) { return text; }
        return tables[FallbackLanguage].TryGetValue(key, out var english) ? english : key;
    }

    /// <summary>Whether <paramref name="language"/> has its own text for <paramref name="key"/>.</summary>
    public static bool Has(string key, string language) =>
        Tables.Value.TryGetValue(language, out var table) && table.ContainsKey(key);

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Every key of the English reference table.</summary>
    public static IEnumerable<string> Keys => Tables.Value[FallbackLanguage].Keys;

    public static string SourceName(string logSource) =>
        Has("Source." + logSource, FallbackLanguage) ? Get("Source." + logSource) : logSource;

    public static string ReasonName(string? reason) =>
        reason is null ? string.Empty : Has("Reason." + reason, FallbackLanguage) ? Get("Reason." + reason) : reason;

    /// <summary>CLDR plural category of <paramref name="n"/> in <paramref name="language"/> (the languages bundled here).</summary>
    public static string PluralCategory(string language, long n) => language switch
    {
        "ja" or "zh" or "zh-Hant" or "ko" or "vi" or "id" or "tr" => "other",
        "fr" or "hi" => n is 0 or 1 ? "one" : "other",
        "ru" or "uk" or "pl" => SlavicCategory(n, oneEndsInOne: language != "pl"),
        "sk" => n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "many",
        _ => n == 1 ? "one" : "other",
    };

    /// <summary>
    /// East Slavic (ru, uk: 1, 21, 31… are "one") and Polish (only 1 is "one") rules: numbers
    /// ending in 2–4, except 12–14, are "few"; the rest are "many".
    /// </summary>
    private static string SlavicCategory(long n, bool oneEndsInOne)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        if (oneEndsInOne ? mod10 == 1 && mod100 != 11 : n == 1) { return "one"; }
        return mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? "few" : "many";
    }

    /// <summary>A unit (<c>hour</c>, <c>minute</c>, <c>second</c>) with the plural form <paramref name="n"/> needs.</summary>
    public static string Unit(string unit, long n)
    {
        var language = Language;
        var key = $"Unit.{unit}.{PluralCategory(language, n)}";
        var text = Has(key, language) ? Get(key, language) : Get($"Unit.{unit}.other", language);
        return string.Format(CultureInfo.CurrentCulture, text, n);
    }

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var assembly = typeof(Strings).Assembly;
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            const string prefix = "Localization.";
            if (!resource.StartsWith(prefix, StringComparison.Ordinal) || !resource.EndsWith(".json", StringComparison.Ordinal)) { continue; }
            var language = resource[prefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(resource)!;
            tables[language] = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        return tables;
    }

    /// <summary>"1 hour, 30 minutes" / "1時間30分"; "Indefinitely" for zero.</summary>
    public static string DurationTitle(ActivationDuration duration) =>
        duration.IsIndefinite ? Get("Indefinitely") : DurationText(TimeSpan.FromSeconds(duration.Seconds));

    public static string DurationText(TimeSpan span)
    {
        var parts = new List<string>();
        var hours = (long)span.TotalHours;
        if (hours > 0) { parts.Add(Unit("hour", hours)); }
        if (span.Minutes > 0) { parts.Add(Unit("minute", span.Minutes)); }
        if (span.Seconds > 0) { parts.Add(Unit("second", span.Seconds)); }
        if (parts.Count == 0) { parts.Add(Unit("second", 0)); }
        return string.Join(Get("Unit.separator"), parts);
    }

    /// <summary>Countdown text, e.g. <c>1:05:09</c> or <c>4:59</c>.</summary>
    public static string Countdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) { remaining = TimeSpan.Zero; }
        // Round up so "0:00" only shows when the session has ended.
        var total = (long)Math.Ceiling(remaining.TotalSeconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>Short weekday name in the UI language (from its culture data).</summary>
    public static string WeekdayName(DayOfWeek day)
    {
        var culture = CultureInfo.GetCultureInfo(Language == "zh" ? "zh-Hans" : Language);
        var names = Language is "ja" or "zh" or "zh-Hant"
            ? culture.DateTimeFormat.ShortestDayNames // 月, 火 … instead of 月曜 / 周一
            : culture.DateTimeFormat.AbbreviatedDayNames;
        return names[(int)day];
    }
}
