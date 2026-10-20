using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeepingYouAwake.Core;

/// <summary>
/// Every user setting, persisted as JSON in <c>%APPDATA%\KeepingYouAwake\settings.json</c>.
/// Instances handed out by <see cref="SettingsStore"/> are snapshots; change settings
/// through <see cref="SettingsStore.Update"/>.
/// </summary>
public sealed class AppSettings
{
    public const double MinimumBatteryThreshold = 10;
    public const double MaximumBatteryThreshold = 90;
    public const double DefaultCpuLoadThreshold = 50;

    // General
    public bool ActivateOnLaunch { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool CountdownHidden { get; set; }

    // Durations
    public List<long> Durations { get; set; } = [];
    public long DefaultDurationSeconds { get; set; }

    // Battery
    public bool BatteryCapacityThresholdEnabled { get; set; }
    public double BatteryCapacityThreshold { get; set; } = MinimumBatteryThreshold;
    public bool BatterySaverMonitoringEnabled { get; set; }

    // Advanced
    public bool QuitOnTimerExpiration { get; set; }
    public bool AllowDisplaySleep { get; set; }
    public bool PreventDiskSleep { get; set; }
    public bool ActivateOnExternalDisplay { get; set; }
    public bool DeactivateOnUserSwitch { get; set; }
    public bool DeactivateOnFullCharge { get; set; }
    public bool DriveAlive { get; set; }
    public bool ActivateOnACPower { get; set; }
    public bool ScheduleEnabled { get; set; }
    public bool DownloadInProgressActivationEnabled { get; set; }
    public bool MouseJiggler { get; set; }
    public bool ActivateOnExternalAudioOutput { get; set; }
    public bool ActivateOnCpuLoad { get; set; }
    public double CpuLoadThreshold { get; set; } = DefaultCpuLoadThreshold;

    // Watched items
    public List<string> WatchedWiFiSsids { get; set; } = [];
    public List<string> DisabledWatchedWiFiSsids { get; set; } = [];
    public List<string> WatchedApplications { get; set; } = [];
    public List<string> DisabledWatchedApplications { get; set; } = [];
    /// <summary>Null means "the user's Downloads folder"; an empty list means no folder is watched.</summary>
    public List<string>? DownloadDirectories { get; set; }
    public List<string> DisabledDownloadDirectories { get; set; } = [];
    public List<ScheduleWindow> ScheduleWindows { get; set; } = [];

    // Updates
    public bool AutomaticallyCheckForUpdates { get; set; } = true;
    public bool PreReleaseUpdates { get; set; }
    public DateTimeOffset? LastUpdateCheck { get; set; }

    [JsonIgnore]
    public double EffectiveBatteryThreshold =>
        double.IsFinite(BatteryCapacityThreshold)
            ? Math.Clamp(BatteryCapacityThreshold, MinimumBatteryThreshold, MaximumBatteryThreshold)
            : MinimumBatteryThreshold;

    [JsonIgnore]
    public double EffectiveCpuLoadThreshold =>
        double.IsFinite(CpuLoadThreshold) ? Math.Clamp(CpuLoadThreshold, 1, 99) : DefaultCpuLoadThreshold;

    /// <summary>Watched SSIDs that aren't switched off (compared case-insensitively).</summary>
    public IReadOnlyList<string> EnabledWatchedWiFiSsids() =>
        WatchedItems.Enabled(WatchedWiFiSsids, DisabledWatchedWiFiSsids, ignoreCase: true);

    /// <summary>Watched executables that aren't switched off (compared case-insensitively).</summary>
    public IReadOnlyList<string> EnabledWatchedApplications() =>
        WatchedItems.Enabled(WatchedApplications, DisabledWatchedApplications, ignoreCase: true);

    /// <summary>Download folders to watch, expanded. Switching every folder off yields an empty list.</summary>
    public IReadOnlyList<string> EnabledDownloadDirectories() =>
        DownloadDirectories is null
            ? [WatchedItems.DefaultDownloadsFolder]
            : WatchedItems.Enabled(DownloadDirectories, DisabledDownloadDirectories, ignoreCase: true);

    public IReadOnlyList<ScheduleWindow> EnabledScheduleWindows() =>
        ScheduleWindows.Where(w => w.Enabled).ToList();

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions)!;
    }

    /// <summary>Resets the Advanced tab's toggles (Reset to Default button).</summary>
    public void ResetAdvanced()
    {
        var defaults = new AppSettings();
        QuitOnTimerExpiration = defaults.QuitOnTimerExpiration;
        AllowDisplaySleep = defaults.AllowDisplaySleep;
        PreventDiskSleep = defaults.PreventDiskSleep;
        ActivateOnExternalDisplay = defaults.ActivateOnExternalDisplay;
        DeactivateOnUserSwitch = defaults.DeactivateOnUserSwitch;
        DeactivateOnFullCharge = defaults.DeactivateOnFullCharge;
        DriveAlive = defaults.DriveAlive;
        ActivateOnACPower = defaults.ActivateOnACPower;
        CountdownHidden = defaults.CountdownHidden;
        ScheduleEnabled = defaults.ScheduleEnabled;
        DownloadInProgressActivationEnabled = defaults.DownloadInProgressActivationEnabled;
        MouseJiggler = defaults.MouseJiggler;
        ActivateOnExternalAudioOutput = defaults.ActivateOnExternalAudioOutput;
        ActivateOnCpuLoad = defaults.ActivateOnCpuLoad;
        CpuLoadThreshold = defaults.CpuLoadThreshold;
    }

    /// <summary>Drops null / blank entries a hand-edited file may contain.</summary>
    internal void Sanitize()
    {
        static List<string> Clean(List<string>? list) =>
            (list ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        Durations = (Durations ?? []).Where(x => x is > 0 and <= ActivationDuration.MaximumSeconds).Distinct().ToList();
        if (DefaultDurationSeconds is < 0 or > ActivationDuration.MaximumSeconds) { DefaultDurationSeconds = 0; }
        WatchedWiFiSsids = Clean(WatchedWiFiSsids);
        DisabledWatchedWiFiSsids = Clean(DisabledWatchedWiFiSsids);
        WatchedApplications = Clean(WatchedApplications);
        DisabledWatchedApplications = Clean(DisabledWatchedApplications);
        DownloadDirectories = DownloadDirectories is null ? null : Clean(DownloadDirectories);
        DisabledDownloadDirectories = Clean(DisabledDownloadDirectories);
        ScheduleWindows = (ScheduleWindows ?? []).Where(w => w is not null).Select(w => w.Normalized()).ToList();
    }
}

/// <summary>Loads, saves and publishes changes to <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string? filePath;

    /// <param name="filePath">JSON file to persist to, or null to keep settings in memory only (tests).</param>
    public SettingsStore(string? filePath)
    {
        this.filePath = filePath;
        Settings = Load(filePath);
    }

    public static string DefaultFilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public string? FilePath => filePath;

    /// <summary>The current settings. Treat as read-only; use <see cref="Update"/> to change them.</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>Raised after every <see cref="Update"/>, on the caller's thread.</summary>
    public event EventHandler? Changed;

    public void Update(Action<AppSettings> change)
    {
        var updated = Settings.Clone();
        change(updated);
        updated.Sanitize();
        Settings = updated;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-reads the file after an outside edit (the MCP server, a text editor). Raises
    /// <see cref="Changed"/> only when the content differs, so the app's own saves are ignored.
    /// An unreadable file is left alone and the current settings stay in effect.
    /// </summary>
    public bool ReloadFromDisk()
    {
        if (filePath is null || !File.Exists(filePath)) { return false; }
        AppSettings loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), JsonOptions) ?? new AppSettings();
            loaded.Sanitize();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        if (JsonSerializer.Serialize(loaded, JsonOptions) == JsonSerializer.Serialize(Settings, JsonOptions)) { return false; }
        Settings = loaded;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Save()
    {
        if (filePath is null) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings stay in effect for this run; the next change retries the write.
        }
    }

    private static AppSettings Load(string? path)
    {
        if (path is null || !File.Exists(path)) { return new AppSettings(); }
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            loaded.Sanitize();
            return loaded;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Keep the unreadable file for the user instead of overwriting it on the next save.
            try { File.Copy(path, path + ".invalid", overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new AppSettings();
        }
    }
}

public static class AppPaths
{
    /// <summary><c>%APPDATA%\KeepingYouAwake</c>: settings and the activity log.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeepingYouAwake");
}
