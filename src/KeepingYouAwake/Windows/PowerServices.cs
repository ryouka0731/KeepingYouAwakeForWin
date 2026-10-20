using System.ComponentModel;
using KeepingYouAwake.Core.Platform;
using Microsoft.Win32;
using PowerStatus = KeepingYouAwake.Core.Platform.PowerStatus;
using static KeepingYouAwake.Windows.NativeMethods;

namespace KeepingYouAwake.Windows;

/// <summary>
/// Keeps the PC awake with a power request (listed by <c>powercfg /requests</c> with
/// this app's reason), falling back to <c>SetThreadExecutionState</c> on the UI thread.
/// </summary>
internal sealed class PowerRequestAssertion : ISleepAssertion, IDisposable
{
    private IntPtr request;
    private bool systemSet;
    private bool displaySet;
    private bool usingFallback;

    public void Acquire(bool allowDisplaySleep)
    {
        if (request == IntPtr.Zero && !usingFallback)
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = "KeepingYouAwake is preventing sleep.",
            };
            request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1))
            {
                request = IntPtr.Zero;
                usingFallback = true;
            }
        }

        if (usingFallback)
        {
            var flags = ExecutionState.Continuous | ExecutionState.SystemRequired;
            if (!allowDisplaySleep) { flags |= ExecutionState.DisplayRequired; }
            SetThreadExecutionState(flags);
            return;
        }

        if (!systemSet) { systemSet = PowerSetRequest(request, PowerRequestType.SystemRequired); }
        if (!allowDisplaySleep && !displaySet)
        {
            displaySet = PowerSetRequest(request, PowerRequestType.DisplayRequired);
        }
        else if (allowDisplaySleep && displaySet)
        {
            PowerClearRequest(request, PowerRequestType.DisplayRequired);
            displaySet = false;
        }
    }

    public void Release()
    {
        if (usingFallback)
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            return;
        }
        if (request == IntPtr.Zero) { return; }
        if (systemSet) { PowerClearRequest(request, PowerRequestType.SystemRequired); }
        if (displaySet) { PowerClearRequest(request, PowerRequestType.DisplayRequired); }
        systemSet = displaySet = false;
    }

    public void Dispose()
    {
        Release();
        if (request != IntPtr.Zero)
        {
            CloseHandle(request);
            request = IntPtr.Zero;
        }
    }
}

/// <summary>Battery and AC state from <c>GetSystemPowerStatus</c>, refreshed on power broadcasts and every 30 s.</summary>
internal sealed class WindowsPowerSource : IPowerSource, IDisposable
{
    private readonly IDisposable poll;

    public WindowsPowerSource(IScheduler scheduler)
    {
        Current = Read();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        poll = scheduler.Every(TimeSpan.FromSeconds(30), Refresh);
    }

    public PowerStatus Current { get; private set; }
    public event EventHandler? Changed;

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        poll.Dispose();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => UiThread.Post(Refresh);

    private void Refresh()
    {
        var status = Read();
        if (status == Current) { return; }
        Current = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static PowerStatus Read()
    {
        if (!GetSystemPowerStatus(out var s)) { return new PowerStatus(BatteryState.Unknown, 100, false); }
        var saver = s.SystemStatusFlag == 1;
        var noBattery = s.BatteryFlag == 128 || s.BatteryFlag == 255 || s.BatteryLifePercent == 255;
        if (noBattery || s.ACLineStatus == 255) { return new PowerStatus(BatteryState.Unknown, 100, saver); }

        double percent = s.BatteryLifePercent;
        var charging = (s.BatteryFlag & 8) != 0;
        var state = s.ACLineStatus switch
        {
            0 => BatteryState.Unplugged,
            _ when charging && percent < 100 => BatteryState.Charging,
            // On AC and not charging: full, or held at a charge limit.
            _ => BatteryState.Full,
        };
        return new PowerStatus(state, percent, saver);
    }
}

/// <summary>Fast user switching and Remote Desktop connect / disconnect.</summary>
internal sealed class WindowsSessionEvents : ISessionEvents, IDisposable
{
    public WindowsSessionEvents() => SystemEvents.SessionSwitch += OnSessionSwitch;

    public event EventHandler? SessionBecameActive;
    public event EventHandler? SessionResignedActive;

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                UiThread.Post(() => SessionBecameActive?.Invoke(this, EventArgs.Empty));
                break;
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                UiThread.Post(() => SessionResignedActive?.Invoke(this, EventArgs.Empty));
                break;
        }
    }
}

/// <summary>Counts active display paths whose connector isn't built in (eDP / LVDS / internal).</summary>
internal sealed class DisplayConfigMonitor : IDisplayMonitor, IDisposable
{
    public DisplayConfigMonitor()
    {
        ExternalDisplayCount = Count();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public int ExternalDisplayCount { get; private set; }
    public event EventHandler? Changed;

    public void Dispose() => SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => UiThread.Post(() =>
    {
        var count = Count();
        if (count == ExternalDisplayCount) { return; }
        ExternalDisplayCount = count;
        Changed?.Invoke(this, EventArgs.Empty);
    });

    internal static int Count()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) { break; }
            var paths = new byte[pathCount * DisplayConfigPathInfoSize];
            var modes = new byte[modeCount * DisplayConfigModeInfoSize];
            var result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == 122) { continue; } // ERROR_INSUFFICIENT_BUFFER: the topology changed meanwhile
            if (result != 0) { break; }

            var external = 0;
            for (var i = 0; i < pathCount; i++)
            {
                var technology = BitConverter.ToUInt32(paths, i * DisplayConfigPathInfoSize + PathOutputTechnologyOffset);
                // Built-in panels, and virtual displays (Remote Desktop, VMs, virtual display drivers).
                if (technology is not (OutputTechnologyInternal or OutputTechnologyLvds
                    or OutputTechnologyDisplayPortEmbedded or OutputTechnologyUdiEmbedded
                    or OutputTechnologyIndirectVirtual or OutputTechnologyOther))
                {
                    external++;
                }
            }
            return external;
        }
        // Fallback: every screen beyond the first.
        return Math.Max(0, Screen.AllScreens.Length - 1);
    }
}

internal sealed class SystemTimesCpuSampler : ICpuTimesSampler
{
    public CpuTimes? Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) { return null; }
        // Kernel time includes idle time.
        return new CpuTimes(idle.Value, kernel.Value + user.Value);
    }
}

/// <summary>Moves the pointer one pixel and back with real input, resetting the idle timer, then restores it exactly.</summary>
internal sealed class SendInputNudger : IPointerNudger
{
    public void Nudge()
    {
        if (!GetCursorPos(out var here)) { return; }
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        var width = Math.Max(2, GetSystemMetrics(SmCxVirtualScreen));
        var height = Math.Max(2, GetSystemMetrics(SmCyVirtualScreen));
        var x = here.X + 1 < left + width ? here.X + 1 : here.X - 1;

        Input Move(int px, int py) => new()
        {
            Type = InputMouse,
            Mouse = new MouseInput
            {
                Dx = (int)Math.Round((px - left) * 65535.0 / (width - 1)),
                Dy = (int)Math.Round((py - top) * 65535.0 / (height - 1)),
                Flags = MouseEventMove | MouseEventAbsolute | MouseEventVirtualDesk,
            },
        };

        Input[] inputs = [Move(x, here.Y), Move(here.X, here.Y)];
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Input>());
        SetCursorPos(here.X, here.Y);
    }
}

/// <summary>Ready, local fixed and removable drives other than the system drive.</summary>
internal sealed class WindowsDriveEnumerator : IDriveEnumerator
{
    public IReadOnlyList<string> ExternalWritableRoots()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var roots = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) { continue; }
                if (!drive.IsReady) { continue; }
                if (string.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase)) { continue; }
                roots.Add(drive.RootDirectory.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return roots;
    }
}

/// <summary>Executables running in this Windows session.</summary>
internal sealed class SessionProcessLister : IProcessLister
{
    private readonly int sessionId = CurrentSessionId();

    private static int CurrentSessionId()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return self.SessionId;
    }

    public IReadOnlyCollection<string> RunningExecutableNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != sessionId) { continue; }
                    names.Add(process.ProcessName + ".exe");
                }
                catch (InvalidOperationException) { /* exited meanwhile */ }
            }
        }
        return names;
    }
}
