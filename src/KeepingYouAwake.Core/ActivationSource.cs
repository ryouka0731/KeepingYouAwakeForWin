namespace KeepingYouAwake.Core;

/// <summary>Who started a session. Feature triggers may only end the sessions they started.</summary>
public enum ActivationSource
{
    User,
    WatchedApp,
    WatchedSsid,
    ACPower,
    ExternalDisplay,
    Schedule,
    Download,
    AudioOutput,
    CpuLoad,
}

public static class ActivationSourceExtensions
{
    /// <summary>The stable string written to the activity log's <c>source</c> field.</summary>
    public static string ToLogString(this ActivationSource source) => source switch
    {
        ActivationSource.WatchedApp => ActivityLogSources.WatchedApp,
        ActivationSource.WatchedSsid => ActivityLogSources.WatchedSsid,
        ActivationSource.ACPower => ActivityLogSources.ACPower,
        ActivationSource.ExternalDisplay => ActivityLogSources.ExternalDisplay,
        ActivationSource.Schedule => ActivityLogSources.Schedule,
        ActivationSource.Download => ActivityLogSources.Download,
        ActivationSource.AudioOutput => ActivityLogSources.AudioOutput,
        ActivationSource.CpuLoad => ActivityLogSources.CpuLoad,
        _ => ActivityLogSources.User,
    };
}

/// <summary>
/// Tracks the source of the running session and encodes the invariant
/// "a feature trigger never deactivates a user-initiated session":
/// triggers end sessions only through <see cref="TerminateIfOwnedBy"/>.
/// </summary>
public sealed class ActivationOwnership
{
    public bool IsActive { get; private set; }

    /// <summary>Only meaningful while <see cref="IsActive"/>; <see cref="ActivationSource.User"/> otherwise.</summary>
    public ActivationSource Source { get; private set; } = ActivationSource.User;

    public void Start(ActivationSource source)
    {
        IsActive = true;
        Source = source;
    }

    public void Terminate()
    {
        IsActive = false;
        Source = ActivationSource.User;
    }

    /// <summary>Clears ownership and returns true only when the active session belongs to <paramref name="source"/>.</summary>
    public bool TerminateIfOwnedBy(ActivationSource source)
    {
        if (!IsActive || Source != source) { return false; }
        Terminate();
        return true;
    }
}
