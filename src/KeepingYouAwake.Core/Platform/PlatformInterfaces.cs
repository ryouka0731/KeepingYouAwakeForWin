namespace KeepingYouAwake.Core.Platform;

/// <summary>Holds the OS "don't sleep" request for the running session.</summary>
public interface ISleepAssertion
{
    /// <summary>Requests that the system stays awake; the display too unless <paramref name="allowDisplaySleep"/>.</summary>
    void Acquire(bool allowDisplaySleep);

    /// <summary>Releases the request. Safe to call when nothing is held.</summary>
    void Release();
}

public enum BatteryState
{
    /// <summary>No battery (desktop PC) or the state can't be read.</summary>
    Unknown,
    /// <summary>Running on battery.</summary>
    Unplugged,
    /// <summary>On AC power, charging.</summary>
    Charging,
    /// <summary>On AC power, fully charged (or not charging).</summary>
    Full,
}

public readonly record struct PowerStatus(BatteryState State, double CapacityPercent, bool BatterySaverEnabled)
{
    public bool IsOnACPower => State is BatteryState.Charging or BatteryState.Full;
}

public interface IPowerSource
{
    PowerStatus Current { get; }

    /// <summary>Raised on the UI thread when AC state, capacity or battery saver changes.</summary>
    event EventHandler? Changed;
}

/// <summary>Fast user switching / remote session connect and disconnect.</summary>
public interface ISessionEvents
{
    /// <summary>This user's session became the active console session again.</summary>
    event EventHandler? SessionBecameActive;

    /// <summary>Another user took over the console (or this session was disconnected).</summary>
    event EventHandler? SessionResignedActive;
}

public interface IDisplayMonitor
{
    /// <summary>Number of connected displays that are not built into the device.</summary>
    int ExternalDisplayCount { get; }

    /// <summary>Raised on the UI thread when the display configuration changes.</summary>
    event EventHandler? Changed;
}

/// <summary>Polled by Core; implementations only answer the question once.</summary>
public interface IWiFiProbe
{
    /// <summary>SSIDs of the Wi-Fi networks currently joined (one per adapter); empty when none or unreadable.</summary>
    IReadOnlyList<string> CurrentSsids();
}

public interface IProcessLister
{
    /// <summary>Executable names (e.g. <c>obs64.exe</c>) of the processes running in this session.</summary>
    IReadOnlyCollection<string> RunningExecutableNames();
}

public interface IAudioOutputProbe
{
    /// <summary>
    /// Whether the default playback device is external (Bluetooth, USB, HDMI/DisplayPort,
    /// network…) rather than built-in speakers or headphone jack. Null when unknown.
    /// </summary>
    bool? IsDefaultOutputExternal();
}

public readonly record struct CpuTimes(ulong Idle, ulong Total);

public interface ICpuTimesSampler
{
    /// <summary>Cumulative idle and total CPU time across all cores, or null when unavailable.</summary>
    CpuTimes? Sample();
}

public interface IPointerNudger
{
    /// <summary>Moves the pointer by one pixel and back, resetting the system idle timer.</summary>
    void Nudge();
}

public interface IDriveEnumerator
{
    /// <summary>Root directories of the local, writable, non-system drives to keep spinning.</summary>
    IReadOnlyList<string> ExternalWritableRoots();
}
