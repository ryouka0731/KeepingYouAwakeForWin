using System.Diagnostics;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.Tests;

/// <summary>
/// The Win32 implementations against the real OS. CI machines have no battery, Wi-Fi
/// or speakers, so these check that every probe answers sensibly instead of throwing.
/// </summary>
public class PlatformTests
{
    [Fact]
    public void PowerRequestIsAcquiredAndReleased()
    {
        using var assertion = new PowerRequestAssertion();
        assertion.Release(); // nothing held yet
        assertion.Acquire(allowDisplaySleep: false);
        Assert.True(assertion.IsHeld);
        assertion.Acquire(allowDisplaySleep: true); // switches the display request off
        Assert.True(assertion.IsHeld);
        assertion.Release();
        Assert.False(assertion.IsHeld);
    }

    [Fact]
    public void ProbesAnswerWithoutThrowing()
    {
        var power = WindowsPowerSource.Read();
        Assert.InRange(power.CapacityPercent, 0, 100);
        Assert.True(DisplayConfigMonitor.Count() >= 0);

        var wifi = WlanWiFiProbe.Read();
        Assert.True(Enum.IsDefined(wifi.Status));
        Assert.Equal(wifi.Status == WiFiReadStatus.Connected, wifi.Ssids.Count > 0);
        Assert.NotNull(new WlanWiFiProbe().CurrentSsids());

        _ = new CoreAudioProbe().IsDefaultOutputExternal(); // false / null on machines without audio
    }

    [Fact]
    public void CpuSamplerAdvances()
    {
        var sampler = new SystemTimesCpuSampler();
        var first = sampler.Sample()!.Value;
        Thread.SpinWait(5_000_000);
        Thread.Sleep(50);
        var second = sampler.Sample()!.Value;
        Assert.True(second.Total > first.Total);
        Assert.True(second.Idle <= second.Total);
        Assert.InRange(CpuLoadMonitor.BusyPercent(first, second) ?? 0, 0, 100);
    }

    [Fact]
    public void ProcessListerSeesThisProcess()
    {
        using var self = Process.GetCurrentProcess();
        Assert.Contains(self.ProcessName + ".exe", new SessionProcessLister().RunningExecutableNames(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DriveEnumeratorSkipsTheSystemDrive()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var roots = new WindowsDriveEnumerator().ExternalWritableRoots();
        Assert.DoesNotContain(roots, r => string.Equals(r, systemRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NudgerLeavesThePointerWhereItWas()
    {
        if (!NativeMethods.GetCursorPos(out var before)) { return; } // no interactive desktop
        new SendInputNudger().Nudge();
        Assert.True(NativeMethods.GetCursorPos(out var after));
        Assert.Equal((before.X, before.Y), (after.X, after.Y));
    }

    [Fact]
    public void PowerSourceAndMonitorsSubscribeAndUnsubscribe() => Sta.Run(() =>
    {
        var scheduler = new WinFormsScheduler();
        using var power = new WindowsPowerSource(scheduler);
        Assert.Equal(WindowsPowerSource.Read(), power.Current);
        using var displays = new DisplayConfigMonitor();
        Assert.True(displays.ExternalDisplayCount >= 0);
        using var sessions = new WindowsSessionEvents();
    });
}

public class ShellIntegrationTests
{
    [Fact]
    public void RegistersTheUrlSchemeAndStartAtLogin()
    {
        using var sandbox = new RegistrySandbox();
        var shell = sandbox.Shell;
        Assert.Null(shell.RegisteredUrlCommand());
        Assert.True(shell.RegisterUrlScheme());
        Assert.Equal("\"C:\\Apps\\KYA\\KeepingYouAwake.exe\" \"%1\"", shell.RegisteredUrlCommand());

        Assert.False(shell.IsStartAtLoginEnabled);
        shell.RefreshStartAtLoginPath(); // disabled: stays disabled
        Assert.False(shell.IsStartAtLoginEnabled);
        shell.SetStartAtLogin(true);
        Assert.True(shell.IsStartAtLoginEnabled);
        shell.RefreshStartAtLoginPath();
        Assert.True(shell.IsStartAtLoginEnabled);
        shell.SetStartAtLogin(false);
        shell.SetStartAtLogin(false); // missing value is fine
        Assert.False(shell.IsStartAtLoginEnabled);
    }
}

public class SingleInstanceTests
{
    [Fact]
    public void SecondInstanceForwardsItsArguments() => Sta.Run(() =>
    {
        var name = "KeepingYouAwake-test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstance.Acquire(name, [])!;
        Assert.NotNull(first);
        string[]? received = null;
        first.Listen(args => received = args);

        // A second "process": acquiring from another thread can't take the mutex.
        SingleInstance? second = null;
        var other = new Thread(() => second = SingleInstance.Acquire(name, ["--activate", "60"]));
        other.Start();
        other.Join();
        Assert.Null(second);

        Sta.PumpUntil(() => received is not null, 10_000);
        Assert.Equal(["--activate", "60"], received!);
    });

    [Fact]
    public void PipeNameIsPerUserAndSession() =>
        Assert.Matches(@"^KeepingYouAwake-S-1-[\d-]+-\d+$", SingleInstance.Name);
}

public class WinFormsSchedulerTests
{
    [Fact]
    public void TimersFireOnTheUiThreadAndCanBeCancelled() => Sta.Run(() =>
    {
        var scheduler = new WinFormsScheduler();
        var uiThread = Environment.CurrentManagedThreadId;
        var once = 0;
        var repeats = 0;
        var cancelledFired = false;
        var onUiThread = true;

        scheduler.After(TimeSpan.FromMilliseconds(50), () =>
        {
            once++;
            onUiThread &= Environment.CurrentManagedThreadId == uiThread;
        });
        using var every = scheduler.Every(TimeSpan.FromMilliseconds(30), () => repeats++);
        scheduler.After(TimeSpan.FromMilliseconds(40), () => cancelledFired = true).Dispose();

        Sta.PumpUntil(() => once == 1 && repeats >= 3);
        Sta.Pump(100);
        Assert.Equal(1, once);
        Assert.True(onUiThread);
        Assert.False(cancelledFired);
        Assert.InRange(scheduler.Now, DateTimeOffset.Now.AddSeconds(-5), DateTimeOffset.Now.AddSeconds(5));
    });

    [Fact]
    public void UiThreadPostRunsOnTheCapturedContext() => Sta.Run(() =>
    {
        var ran = false;
        Task.Run(() => UiThread.Post(() => ran = true)).Wait();
        Sta.PumpUntil(() => ran);
    });
}
