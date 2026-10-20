using System.Runtime.ExceptionServices;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Tests;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;
using Microsoft.Win32;

// The app has one UI thread, and UiThread / SystemEvents are process-wide: run the
// WinForms tests one at a time, each on its own STA thread.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace KeepingYouAwake.Tests;

/// <summary>Runs WinForms code on a dedicated STA thread with a message-loop context, like the app's UI thread.</summary>
internal static class Sta
{
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            using var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            UiThread.Capture();
            try { body(); }
            catch (Exception e) { failure = ExceptionDispatchInfo.Capture(e); } // rethrown on the test thread
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
        {
            throw new TimeoutException("STA test body did not finish. Dialog log: " + string.Join(" | ", DialogLog));
        }
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

    /// <summary>
    /// Clicks a button the way a user would: its tab page is brought to the front first,
    /// because WinForms ignores <see cref="Button.PerformClick"/> on hidden controls.
    /// </summary>
    public static void Click(Button button)
    {
        for (Control? parent = button.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is TabPage page && page.Parent is TabControl tabs) { tabs.SelectedTab = page; }
        }
        Application.DoEvents();
        Assert.True(button.CanSelect, $"button '{button.Text}' is not clickable");
        button.PerformClick();
    }

    /// <summary>
    /// Runs <paramref name="answer"/> on the next <typeparamref name="T"/> form once it is shown —
    /// i.e. from inside its modal loop, so code calling <c>ShowDialog</c> can be tested.
    /// </summary>
    public static void WhenFormShown<T>(Action<T> answer) where T : Form =>
        Poll(() =>
        {
            if (Application.OpenForms.OfType<T>().FirstOrDefault(f => f.Visible) is not { } form) { return false; }
            answer(form);
            return true;
        });

    public const int IdOk = 1;
    public const int IdCancel = 2;

    /// <summary>What the native-dialog helper saw and did, reported when a test times out.</summary>
    public static System.Collections.Concurrent.ConcurrentQueue<string> DialogLog { get; } = new();

    private const string ClaimProperty = "KeepingYouAwake.Tests.Claimed";
    private static int nextClaim;

    /// <summary>
    /// Presses <paramref name="buttonId"/> on the next native dialog of this thread (MessageBox, file / folder
    /// pickers). Keeps pressing until that dialog is gone: a dialog still initializing ignores early input.
    /// </summary>
    public static void WhenNativeDialogShown(int buttonId)
    {
        var dialog = IntPtr.Zero;
        var claim = new IntPtr(++nextClaim);
        var attempts = 0;
        Poll(() =>
        {
            if (dialog == IntPtr.Zero)
            {
                dialog = FindUnclaimedDialog();
                if (dialog == IntPtr.Zero) { return false; }
                DialogLog.Enqueue($"claim {dialog:X} for {buttonId}");
                // Tagged on the window itself: a later dialog may reuse the same handle value.
                SetProp(dialog, ClaimProperty, claim);
            }
            if (!IsWindow(dialog) || GetProp(dialog, ClaimProperty) != claim)
            {
                DialogLog.Enqueue($"gone {dialog:X}");
                return true;
            }
            DialogLog.Enqueue($"post {buttonId} to {dialog:X}");
            PostMessage(dialog, WmCommand, new IntPtr(buttonId), IntPtr.Zero);
            // A message box with a single OK button doesn't answer to IDOK; closing it is the same as OK.
            if (buttonId == IdCancel || ++attempts > 3) { PostMessage(dialog, WmClose, IntPtr.Zero, IntPtr.Zero); }
            return false;
        }, intervalMilliseconds: 150);
    }

    private static IntPtr FindUnclaimedDialog()
    {
        var found = IntPtr.Zero;
        var seen = new List<string>();
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            var name = new System.Text.StringBuilder(64);
            GetClassName(hwnd, name, name.Capacity);
            seen.Add($"{name}{(IsWindowVisible(hwnd) ? "" : "(hidden)")}");
            if (name.ToString() == "#32770" && IsWindowVisible(hwnd) && GetProp(hwnd, ClaimProperty) == IntPtr.Zero) { found = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero && seen.Count > 0 && DialogLog.Count < 200) { DialogLog.Enqueue("scan: " + string.Join(",", seen.Distinct())); }
        return found;
    }

    /// <summary>Calls <paramref name="tick"/> on this thread periodically (also inside modal loops) until it returns true.</summary>
    private static void Poll(Func<bool> tick, int intervalMilliseconds = 20)
    {
        var timer = new System.Windows.Forms.Timer { Interval = intervalMilliseconds };
        var deadline = Environment.TickCount64 + 15_000;
        timer.Tick += (_, _) =>
        {
            if (!tick() && Environment.TickCount64 < deadline) { return; }
            timer.Stop();
            timer.Dispose();
        };
        timer.Start();
    }

    private const int WmCommand = 0x0111;
    private const int WmClose = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr hwnd, string name);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(uint threadId, EnumWindowsProc callback, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int capacity);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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

    public TrayHarness(Action<AppSettings>? configure = null, HttpMessageHandler? updateHandler = null, IUserPrompts? prompts = null)
    {
        Fakes = new ControllerHarness(configure);
        // The harness's own controller isn't used; the tray builds one on the same fakes.
        Fakes.Controller.Dispose();
        Context = new TrayApplicationContext(Fakes.Store, Fakes.Log,
            new TrayOptions(Platform, registry.Shell, BackgroundUpdateChecks: false, updateHandler ?? new FakeReleasesHandler("[]"), prompts));
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
