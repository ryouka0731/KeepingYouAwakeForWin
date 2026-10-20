using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using PowerStatus = KeepingYouAwake.Core.Platform.PowerStatus;

namespace KeepingYouAwake.Core.Tests;

/// <summary>A scheduler whose clock only moves when <see cref="Advance"/> is called.</summary>
internal sealed class FakeScheduler : IScheduler
{
    private readonly List<Entry> entries = [];

    public DateTimeOffset Now { get; private set; } = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    public int ActiveTimers => entries.Count(e => !e.Cancelled);

    public IDisposable After(TimeSpan delay, Action action) => Add(delay, null, action);

    public IDisposable Every(TimeSpan interval, Action action) => Add(interval, interval, action);

    public void Advance(TimeSpan span)
    {
        var target = Now + span;
        while (true)
        {
            var next = entries.Where(e => !e.Cancelled && e.Due <= target).OrderBy(e => e.Due).FirstOrDefault();
            if (next is null) { break; }
            Now = next.Due;
            if (next.Repeat is { } repeat) { next.Due += repeat; }
            else { next.Cancelled = true; }
            next.Action();
        }
        Now = target;
        entries.RemoveAll(e => e.Cancelled);
    }

    private Entry Add(TimeSpan delay, TimeSpan? repeat, Action action)
    {
        var entry = new Entry { Due = Now + delay, Repeat = repeat, Action = action };
        entries.Add(entry);
        return entry;
    }

    private sealed class Entry : IDisposable
    {
        public DateTimeOffset Due;
        public TimeSpan? Repeat;
        public Action Action = () => { };
        public bool Cancelled;
        public void Dispose() => Cancelled = true;
    }
}

internal sealed class FakeSleepAssertion : ISleepAssertion
{
    public bool Held { get; private set; }
    public bool? AllowDisplaySleep { get; private set; }
    public int AcquireCount { get; private set; }

    public void Acquire(bool allowDisplaySleep)
    {
        Held = true;
        AllowDisplaySleep = allowDisplaySleep;
        AcquireCount++;
    }

    public void Release() => Held = false;
}

internal sealed class FakePowerSource : IPowerSource
{
    public PowerStatus Current { get; private set; } = new(BatteryState.Unknown, 100, false);
    public event EventHandler? Changed;

    public void Set(BatteryState state, double capacity = 80, bool saver = false)
    {
        Current = new PowerStatus(state, capacity, saver);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeSessionEvents : ISessionEvents
{
    public event EventHandler? SessionBecameActive;
    public event EventHandler? SessionResignedActive;
    public void BecomeActive() => SessionBecameActive?.Invoke(this, EventArgs.Empty);
    public void ResignActive() => SessionResignedActive?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeDisplayMonitor : IDisplayMonitor
{
    public int ExternalDisplayCount { get; private set; }
    public event EventHandler? Changed;

    public void Set(int count)
    {
        ExternalDisplayCount = count;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeWiFiProbe : IWiFiProbe
{
    public List<string> Ssids { get; } = [];
    public IReadOnlyList<string> CurrentSsids() => Ssids.ToList();
}

internal sealed class FakeProcessLister : IProcessLister
{
    public List<string> Running { get; } = [];
    public IReadOnlyCollection<string> RunningExecutableNames() => Running.ToList();
}

internal sealed class FakeAudioProbe : IAudioOutputProbe
{
    public bool? External { get; set; } = false;
    public bool? IsDefaultOutputExternal() => External;
}

internal sealed class FakeCpuSampler : ICpuTimesSampler
{
    private ulong idle;
    private ulong total;

    /// <summary>Busy percentage reported for each following 5-second sample.</summary>
    public double BusyPercent { get; set; }

    public CpuTimes? Sample()
    {
        const ulong slice = 1000;
        total += slice;
        idle += (ulong)Math.Round(slice * (100 - BusyPercent) / 100);
        return new CpuTimes(idle, total);
    }
}

internal sealed class FakeNudger : IPointerNudger
{
    public int Count { get; private set; }
    public void Nudge() => Count++;
}

internal sealed class FakeDriveEnumerator : IDriveEnumerator
{
    public List<string> Roots { get; } = [];
    public IReadOnlyList<string> ExternalWritableRoots() => Roots.ToList();
}

/// <summary>An <see cref="AppController"/> wired to fakes, with a temp activity log.</summary>
internal sealed class ControllerHarness : IDisposable
{
    public FakeScheduler Scheduler { get; } = new();
    public FakeSleepAssertion Assertion { get; } = new();
    public FakePowerSource Power { get; } = new();
    public FakeSessionEvents Sessions { get; } = new();
    public FakeDisplayMonitor Displays { get; } = new();
    public FakeWiFiProbe WiFi { get; } = new();
    public FakeProcessLister Processes { get; } = new();
    public FakeAudioProbe Audio { get; } = new();
    public FakeCpuSampler Cpu { get; } = new();
    public FakeNudger Nudger { get; } = new();
    public FakeDriveEnumerator Drives { get; } = new();
    public SettingsStore Store { get; } = new(null);
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "kya-tests-" + Guid.NewGuid().ToString("N"));
    public ActivityLogger Log { get; }
    public AppController Controller { get; }
    public List<SessionNotification> Notifications { get; } = [];
    public int QuitRequests { get; private set; }

    public ControllerHarness(Action<AppSettings>? configure = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        if (configure is not null) { Store.Update(configure); }
        Log = new ActivityLogger(Path.Combine(Directory, "activity.jsonl"), clock: () => Scheduler.Now);
        Controller = new AppController(
            new PlatformServices(Scheduler, Assertion, Power, Sessions, Displays, WiFi, Processes, Audio, Cpu, Nudger, Drives),
            Store, Log);
        Controller.NotificationRequested += (_, n) => Notifications.Add(n);
        Controller.QuitRequested += (_, _) => QuitRequests++;
    }

    public void Dispose()
    {
        Controller.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Tests that switch <see cref="Strings"/>' language share global state and must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StringsCollection
{
    public const string Name = "Strings";
}
