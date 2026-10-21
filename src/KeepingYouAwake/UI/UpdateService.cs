using System.Reflection;
using KeepingYouAwake.Core;
using Timer = System.Windows.Forms.Timer;

namespace KeepingYouAwake.UI;

/// <summary>Checks GitHub Releases at launch and daily when automatic checks are on.</summary>
internal sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private readonly SettingsStore store;
    private readonly HttpClient http;
    private readonly string repository;
    private readonly Timer timer = new() { Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds };

    /// <summary>Delay before the first background check (the app settles and the network comes up at login).</summary>
    internal TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How often background checks look whether a check is due.</summary>
    internal TimeSpan PollInterval
    {
        get => TimeSpan.FromMilliseconds(timer.Interval);
        init => timer.Interval = (int)value.TotalMilliseconds;
    }
    private Task<ReleaseInfo?>? inFlight;

    /// <summary>Creates the service; nothing is checked until <see cref="StartBackgroundChecks"/> or <see cref="CheckNowAsync"/>.</summary>
    /// <param name="store">Settings holding the automatic-check switches and the last check time.</param>
    /// <param name="handler">HTTP handler (tests); null for the default network stack.</param>
    /// <param name="repository"><c>owner/name</c> of the GitHub repository to query.</param>
    public UpdateService(SettingsStore store, HttpMessageHandler? handler = null, string repository = UpdateChecker.DefaultRepository)
    {
        this.store = store;
        this.repository = repository;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(20);
        timer.Tick += async (_, _) => await CheckIfDueAsync();
    }

    public static string CurrentVersion =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Raised on the UI thread by background checks that find a newer release.</summary>
    public event EventHandler<ReleaseInfo>? UpdateFound;

    public void StartBackgroundChecks()
    {
        timer.Start();
        var initial = new Timer { Interval = Math.Max(1, (int)InitialDelay.TotalMilliseconds) };
        initial.Tick += async (_, _) =>
        {
            initial.Dispose();
            await CheckIfDueAsync();
        };
        initial.Start();
    }

    /// <summary>Runs a check when automatic checks are on and the last success is a day old. Network errors are swallowed.</summary>
    internal async Task CheckIfDueAsync()
    {
        var settings = store.Settings;
        if (!settings.AutomaticallyCheckForUpdates) { return; }
        if (settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < CheckInterval) { return; }
        try
        {
            if (await CheckNowAsync() is { } release) { UpdateFound?.Invoke(this, release); }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Try again on the next tick.
        }
    }

    /// <summary>Queries GitHub. Returns the newer release, or null when up to date. Throws on network errors.</summary>
    public async Task<ReleaseInfo?> CheckNowAsync()
    {
        // A check already running (e.g. the background one) is shared, not reported as "up to date".
        if (inFlight is { } running) { return await running; }
        var task = Run();
        inFlight = task;
        try { return await task; }
        finally
        {
            if (inFlight == task) { inFlight = null; }
        }

        async Task<ReleaseInfo?> Run()
        {
            var release = await new UpdateChecker(http, repository).FindUpdateAsync(CurrentVersion, store.Settings.PreReleaseUpdates);
            store.Update(s => s.LastUpdateCheck = DateTimeOffset.Now);
            return release;
        }
    }

    public void Dispose()
    {
        timer.Dispose();
        http.Dispose();
    }
}
