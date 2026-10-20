using System.Runtime.ExceptionServices;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Tests;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;
using Microsoft.Win32;

namespace KeepingYouAwake.Tests;

/// <summary>Runs WinForms code on a dedicated STA thread with a message-loop context, like the app's UI thread.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            UiThread.Capture();
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) { throw new TimeoutException("STA test body did not finish"); }
        failure?.Throw();
    }

    /// <summary>Pumps messages until <paramref name="condition"/> holds (BeginInvoke callbacks, timers).</summary>
    public static void PumpUntil(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) { throw new TimeoutException("condition not met while pumping messages"); }
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>Pumps messages for a fixed time.</summary>
    public static void Pump(int milliseconds = 100)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}

/// <summary>A registry sandbox under HKCU\Software\KeepingYouAwakeTests\{guid}, deleted afterwards.</summary>
internal sealed class RegistrySandbox : IDisposable
{
    public RegistrySandbox()
    {
        Prefix = $@"Software\KeepingYouAwakeTests\{Guid.NewGuid():N}\";
        Shell = new ShellIntegration(Registry.CurrentUser, @"C:\Apps\KYA\KeepingYouAwake.exe", Prefix);
    }

    public string Prefix { get; }
    public ShellIntegration Shell { get; }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(Prefix.TrimEnd('\\'), throwOnMissingSubKey: false);
}

/// <summary>A tray context wired to fakes, an in-memory settings store and a temp activity log.</summary>
internal sealed class TrayHarness : IDisposable
{
    private readonly RegistrySandbox registry = new();

    public TrayHarness(Action<AppSettings>? configure = null)
    {
        Fakes = new ControllerHarness(configure);
        // The harness's own controller isn't used; the tray builds one on the same fakes.
        Fakes.Controller.Dispose();
        Context = new TrayApplicationContext(Fakes.Store, Fakes.Log, Platform, registry.Shell, backgroundUpdateChecks: false);
    }

    public ControllerHarness Fakes { get; }
    public TrayApplicationContext Context { get; }
    public AppController Controller => Context.Controller;

    private PlatformServices Platform => new(Fakes.Scheduler, Fakes.Assertion, Fakes.Power, Fakes.Sessions, Fakes.Displays,
        Fakes.WiFi, Fakes.Processes, Fakes.Audio, Fakes.Cpu, Fakes.Nudger, Fakes.Drives);

    public void Dispose()
    {
        if (!Context.IsExiting) { Context.ExitThread(); }
        Context.Dispose();
        Fakes.Dispose();
        registry.Dispose();
    }
}
