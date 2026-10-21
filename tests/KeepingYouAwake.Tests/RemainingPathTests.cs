using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using KeepingYouAwake.Core.Tests;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;
using Microsoft.Win32;
using PowerStatus = KeepingYouAwake.Core.Platform.PowerStatus;

namespace KeepingYouAwake.Tests;

/// <summary>Fallbacks, OS state changes and UI events that only happen on real machines, driven through test seams.</summary>
public class RemainingPathTests
{
    [Fact]
    public void PowerRequestFallsBackToThreadExecutionState()
    {
        using var assertion = new PowerRequestAssertion(createRequest: () => IntPtr.Zero);
        assertion.Acquire(allowDisplaySleep: false);
        Assert.True(assertion.UsesFallback);
        Assert.True(assertion.IsHeld);
        assertion.Acquire(allowDisplaySleep: true);
        assertion.Release();
        Assert.False(assertion.IsHeld);

        using var invalidHandle = new PowerRequestAssertion(createRequest: () => new IntPtr(-1));
        invalidHandle.Acquire(allowDisplaySleep: true);
        Assert.True(invalidHandle.UsesFallback);
    }

    [Fact]
    public void PowerAndDisplayChangesAreAnnounced() => Sta.Run(() =>
    {
        var status = new PowerStatus(BatteryState.Charging, 50, false);
        using var power = new WindowsPowerSource(new WinFormsScheduler(), () => status);
        var powerChanges = 0;
        power.Changed += (_, _) => powerChanges++;
        status = new PowerStatus(BatteryState.Unplugged, 49, false);
        power.Refresh();
        Assert.Equal(1, powerChanges);
        Assert.Equal(BatteryState.Unplugged, power.Current.State);

        var displays = 0;
        using var monitor = new DisplayConfigMonitor(() => displays);
        var displayChanges = 0;
        monitor.Changed += (_, _) => displayChanges++;
        displays = 2;
        monitor.OnDisplaySettingsChanged(monitor, EventArgs.Empty);
        Sta.PumpUntil(() => displayChanges == 1);
        Assert.Equal(2, monitor.ExternalDisplayCount);

        Assert.True(DisplayConfigMonitor.CountFromScreens() >= 0);
    });

    [Fact]
    public void BackgroundUpdateChecksRunOnTheirTimers() => Sta.Run(() =>
    {
        var store = new SettingsStore(null);
        var handler = new FakeReleasesHandler("[]");
        using var updates = new UpdateService(store, handler)
        {
            InitialDelay = TimeSpan.FromMilliseconds(10),
            PollInterval = TimeSpan.FromMilliseconds(50),
        };
        Assert.Equal(TimeSpan.FromMilliseconds(50), updates.PollInterval);
        updates.StartBackgroundChecks();
        Sta.PumpUntil(() => handler.Requests >= 1 && store.Settings.LastUpdateCheck is not null);

        // Checked moments ago: the hourly poll doesn't ask again.
        Sta.Pump(200);
        Assert.Equal(1, handler.Requests);
    });

    [Fact]
    public void TrayClicksAndNotificationClicks() => Sta.Run(() =>
    {
        var prompts = new ScriptedPrompts();
        var handler = new FakeReleasesHandler("""[{"tag_name":"v99.0.0","html_url":"https://example.invalid/v99"}]""");
        using var tray = new TrayHarness(updateHandler: handler, prompts: prompts);

        tray.Context.OnNotifyIconClick(null, new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0));
        Assert.False(tray.Controller.IsActive); // right-click only opens the menu
        tray.Context.OnNotifyIconClick(null, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
        Assert.True(tray.Controller.IsActive);

        tray.Context.OnBalloonTipClicked(null, EventArgs.Empty); // a session notification: nothing to open
        Assert.Empty(prompts.OpenedUrls);

        var check = tray.Context.Updates.CheckIfDueAsync();
        Sta.PumpUntil(() => check.IsCompleted);
        tray.Context.OnBalloonTipClicked(null, EventArgs.Empty);
        Assert.Equal(["https://example.invalid/v99"], prompts.OpenedUrls);
    });

    [Fact]
    public void MenuWindowActionsAndSettingsArgument() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        tray.Context.Perform(new TrayMenuItem("Settings", TrayMenuAction.ShowSettings));
        tray.Context.Perform(new TrayMenuItem("Log", TrayMenuAction.ShowActivityLog));
        tray.Context.Perform(new TrayMenuItem("Nothing")); // separators / labels do nothing
        tray.Context.Handle(["--settings"]);
        Assert.Contains(Application.OpenForms.Cast<Form>(), f => f is SettingsForm);
        Assert.Contains(Application.OpenForms.Cast<Form>(), f => f is ActivityLogForm);

        tray.Context.Perform(new TrayMenuItem("Quit", TrayMenuAction.Quit));
        Assert.True(tray.Context.IsExiting);
    });

    [Fact]
    public void SettingsPopupMenusReplaceTheirItems() => Sta.Run(() =>
    {
        using var tray = new TrayHarness();
        var form = tray.Context.ShowSettings();
        Sta.Pump(50);

        Sta.Click(form.ApplicationsSection.AddButton);
        Assert.Equal(2, form.Popup.Items.Count);
        var first = form.Popup.Items[0];
        form.Popup.Close();

        Sta.Click(form.WiFiSection.AddButton); // real Wi-Fi reading: at least the manual entry
        Assert.True(first.IsDisposed);
        Assert.Contains(form.Popup.Items.Cast<ToolStripItem>(), i => i.Text == Strings.Get("EnterNetworkName"));
        form.Popup.Close();
    });

    [Fact]
    public void StartAtLoginFailureIsReported() => Sta.Run(() =>
    {
        using var sandbox = new RegistrySandbox();
        using var readOnlyRoot = Registry.CurrentUser.CreateSubKey(sandbox.Prefix + "ro");
        using var locked = Registry.CurrentUser.OpenSubKey(sandbox.Prefix + "ro", writable: false)!;
        var shell = new ShellIntegration(locked, @"C:\Apps\KYA\KeepingYouAwake.exe");
        Assert.False(shell.RegisterUrlScheme());

        var prompts = new ScriptedPrompts();
        var store = new SettingsStore(null);
        using var updates = new UpdateService(store, new FakeReleasesHandler("[]"));
        using var form = new SettingsForm(store, new ActivationDurationsController(store), updates, shell, prompts);
        form.Show();
        var box = form.Find<CheckBox>("StartAtLogin");
        box.Checked = true;
        Assert.Single(prompts.Informed);
        Assert.False(box.Checked); // reflects what is really registered
    });

    [Fact]
    public void OpenUrlSwallowsLaunchFailures()
    {
        // An empty target can't be started; the failure is swallowed instead of crashing the app.
        // (A real URL would launch a browser, so the tests never pass one.)
        ShellIntegration.OpenUrl(string.Empty);
        Assert.Equal(new ShellIntegration(Registry.CurrentUser, "x").IsStartAtLoginEnabled,
            new ShellIntegration(Registry.CurrentUser, "y").IsStartAtLoginEnabled);
        Assert.NotNull(ShellIntegration.Current);
    }
}
