namespace KeepingYouAwake.Core;

public static class WatchedItems
{
    public static string DefaultDownloadsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary><paramref name="items"/> without the entries in <paramref name="disabled"/>.</summary>
    public static IReadOnlyList<string> Enabled(IEnumerable<string> items, IEnumerable<string> disabled, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var off = new HashSet<string>(disabled, comparer);
        return items.Where(item => !off.Contains(item)).ToList();
    }

    /// <summary>
    /// Normalizes a watched application entry: a full path or a bare name becomes
    /// the executable file name with an <c>.exe</c> extension (<c>C:\…\obs64.exe</c> → <c>obs64.exe</c>).
    /// </summary>
    public static string NormalizeExecutableName(string entry)
    {
        var name = entry.Trim().Trim('"');
        name = name.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) { name = name[(slash + 1)..]; }
        if (name.Length > 0 && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { name += ".exe"; }
        return name;
    }

    /// <summary>Whether any running executable matches a watched entry (case-insensitive, <c>.exe</c> optional).</summary>
    public static bool AnyRunning(IEnumerable<string> watched, IEnumerable<string> runningExecutableNames)
    {
        var running = new HashSet<string>(
            runningExecutableNames.Where(n => !string.IsNullOrEmpty(n)).Select(NormalizeExecutableName),
            StringComparer.OrdinalIgnoreCase);
        return watched.Where(w => !string.IsNullOrWhiteSpace(w))
                      .Any(w => running.Contains(NormalizeExecutableName(w)));
    }

    /// <summary>Whether any joined SSID is watched. SSIDs compare case-insensitively.</summary>
    public static bool AnyJoined(IEnumerable<string> watchedSsids, IEnumerable<string> joinedSsids)
    {
        var joined = new HashSet<string>(joinedSsids.Where(s => !string.IsNullOrEmpty(s)), StringComparer.OrdinalIgnoreCase);
        return watchedSsids.Any(joined.Contains);
    }
}
