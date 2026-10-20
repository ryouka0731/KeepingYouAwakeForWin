using System.Runtime.InteropServices;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake.Tests;

/// <summary>Scripted answers for the Settings window's modal questions; records what was asked.</summary>
internal sealed class ScriptedPrompts : IUserPrompts
{
    public ActivationDuration? Duration { get; set; }
    public ScheduleWindow? Schedule { get; set; }
    public string? Text { get; set; }
    public string? RunningApp { get; set; }
    public string? File { get; set; }
    public string? Folder { get; set; }
    public bool ConfirmAnswer { get; set; }
    public List<string> Informed { get; } = [];
    public List<string> Confirmed { get; } = [];
    public List<string> OpenedUrls { get; } = [];
    public ScheduleWindow? EditedWindow { get; private set; }

    public ActivationDuration? AskDuration(IWin32Window owner) => Duration;

    public ScheduleWindow? EditScheduleWindow(IWin32Window owner, ScheduleWindow? window)
    {
        EditedWindow = window;
        return Schedule;
    }

    public string? AskText(IWin32Window owner, string title, string label) => Text;
    public string? ChooseRunningApplication(IWin32Window owner) => RunningApp;
    public string? ChooseFile(IWin32Window owner, string title, string filter, string initialDirectory) => File;
    public string? ChooseFolder(IWin32Window owner, string description, string initialDirectory) => Folder;

    public bool Confirm(IWin32Window owner, string message, string title)
    {
        Confirmed.Add(message);
        return ConfirmAnswer;
    }

    public void Inform(IWin32Window owner, string message, string title, MessageBoxIcon icon = MessageBoxIcon.None) => Informed.Add(message);
    public void OpenUrl(string url) => OpenedUrls.Add(url);
}

/// <summary>The Settings window's dialog-driven paths, with scripted answers.</summary>
public class SettingsDialogFlowTests
{
    private static (TrayHarness Tray, SettingsForm Form, ScriptedPrompts Prompts) Open(Action<AppSettings>? configure = null,
        HttpMessageHandler? updates = null)
    {
        var prompts = new ScriptedPrompts();
        var tray = new TrayHarness(configure, updates, prompts);
        var form = tray.Context.ShowSettings();
        Sta.Pump(50);
        return (tray, form, prompts);
    }

    [Fact]
    public void AddingDurations() => Sta.Run(() =>
    {
        var (tray, form, prompts) = Open();
        using var _ = tray;
        var add = form.Find<Button>("AddDuration");

        prompts.Duration = new ActivationDuration(42);
        Sta.Click(add);
        Assert.Contains(new ActivationDuration(42), tray.Controller.Durations.Durations);

        Sta.Click(add); // the same duration again
        Assert.Equal([Strings.Get("DurationAlreadyAdded")], prompts.Informed);

        prompts.Duration = null; // cancelled
        Sta.Click(add);
        Assert.Single(prompts.Informed);
    });

    [Fact]
    public void ResettingDurationsAsksFirst() => Sta.Run(() =>
    {
        var (tray, form, prompts) = Open();
        using var _ = tray;
        tray.Controller.Durations.Add(new ActivationDuration(42));
        var reset = form.Find<Button>("ResetDurations");

        prompts.ConfirmAnswer = false;
        Sta.Click(reset);
        Assert.Contains(new ActivationDuration(42), tray.Controller.Durations.Durations);

        prompts.ConfirmAnswer = true;
        Sta.Click(reset);
        Assert.DoesNotContain(new ActivationDuration(42), tray.Controller.Durations.Durations);
        Assert.Equal(2, prompts.Confirmed.Count);
    });

    [Fact]
    public void WatchedItemsFromDialogs() => Sta.Run(() =>
    {
        var (tray, form, prompts) = Open();
        using var _ = tray;

        // Wi-Fi: typed name and the Location settings link.
        prompts.Text = "Cafe";
        var offline = form.WiFiAddMenuItems(new WiFiReading(WiFiReadStatus.NotConnected, []), form.WiFiSection).ToList();
        offline[^1].PerformClick();
        Assert.Equal(["Cafe"], tray.Fakes.Store.Settings.WatchedWiFiSsids);
        prompts.Text = null;
        offline[^1].PerformClick();
        Assert.Single(tray.Fakes.Store.Settings.WatchedWiFiSsids);
        form.WiFiAddMenuItems(new WiFiReading(WiFiReadStatus.AccessDenied, []), form.WiFiSection).ElementAt(1).PerformClick();
        Assert.Equal(["ms-settings:privacy-location"], prompts.OpenedUrls);

        // Applications: a program file (normalized to its exe name) or a running app.
        var appItems = form.ApplicationAddMenuItems(form.ApplicationsSection).ToList();
        prompts.File = @"C:\Program Files\OBS\bin\64bit\obs64.exe";
        appItems[0].PerformClick();
        prompts.RunningApp = "Teams.exe";
        appItems[1].PerformClick();
        prompts.File = prompts.RunningApp = null;
        appItems[0].PerformClick();
        appItems[1].PerformClick();
        Assert.Equal(["obs64.exe", "Teams.exe"], tray.Fakes.Store.Settings.WatchedApplications);

        // Download folders.
        prompts.Folder = @"D:\Incoming";
        Sta.Click(form.DownloadsSection.AddButton);
        prompts.Folder = null;
        Sta.Click(form.DownloadsSection.AddButton);
        Assert.Equal([@"D:\Incoming"], tray.Fakes.Store.Settings.DownloadDirectories!);
    });

    [Fact]
    public void ActiveHoursAddEditAndRemove() => Sta.Run(() =>
    {
        var (tray, form, prompts) = Open();
        using var _ = tray;
        var morning = new ScheduleWindow { Weekdays = [DayOfWeek.Monday], StartMinutes = 9 * 60, EndMinutes = 12 * 60 };
        var evening = new ScheduleWindow { Weekdays = [DayOfWeek.Friday], StartMinutes = 18 * 60, EndMinutes = 23 * 60 };

        prompts.Schedule = morning;
        Sta.Click(form.Find<Button>("AddActiveHours"));
        Assert.Single(tray.Fakes.Store.Settings.ScheduleWindows);
        Assert.Null(prompts.EditedWindow);

        var list = form.Find<CheckedListBox>("ActiveHours");
        list.SelectedIndex = 0;
        prompts.Schedule = evening;
        Sta.Click(form.Find<Button>("EditActiveHours"));
        Assert.Equal(9 * 60, prompts.EditedWindow!.StartMinutes); // the dialog got the existing window
        Assert.Equal([DayOfWeek.Friday], tray.Fakes.Store.Settings.ScheduleWindows[0].Weekdays);

        prompts.Schedule = null; // cancelled edit changes nothing
        list.SelectedIndex = 0;
        Sta.Click(form.Find<Button>("EditActiveHours"));
        Assert.Equal([DayOfWeek.Friday], tray.Fakes.Store.Settings.ScheduleWindows[0].Weekdays);

        list.SelectedIndex = 0;
        Sta.Click(form.Find<Button>("RemoveActiveHours"));
        Assert.Empty(tray.Fakes.Store.Settings.ScheduleWindows);
        Sta.Click(form.Find<Button>("EditActiveHours")); // nothing selected: no-op
    });

    [Fact]
    public void CheckForUpdatesReportsEveryOutcome() => Sta.Run(() =>
    {
        void Check(SettingsForm form)
        {
            var task = form.CheckForUpdatesAsync();
            Sta.PumpUntil(() => task.IsCompleted);
        }

        var (upToDate, form1, prompts1) = Open(updates: new FakeReleasesHandler("[]"));
        using (upToDate)
        {
            Check(form1);
            Assert.Equal([Strings.Format("UpToDateFormat", UpdateService.CurrentVersion)], prompts1.Informed);
        }

        var (available, form2, prompts2) = Open(updates: new FakeReleasesHandler("""[{"tag_name":"v99.0.0","html_url":"https://example.invalid/v99"}]"""));
        using (available)
        {
            prompts2.ConfirmAnswer = true;
            Check(form2);
            Assert.Equal(["https://example.invalid/v99"], prompts2.OpenedUrls);
        }

        var (failing, form3, prompts3) = Open(updates: new FakeReleasesHandler("down", System.Net.HttpStatusCode.BadGateway));
        using (failing)
        {
            Sta.Click(form3.Find<Button>("CheckNow"));
            Sta.PumpUntil(() => prompts3.Informed.Count == 1);
            Assert.StartsWith(Strings.Format("UpdateCheckFailedFormat", string.Empty).TrimEnd(), prompts3.Informed[0]);
            Sta.PumpUntil(() => form3.Find<Button>("CheckNow").Enabled);
        }
    });

    [Fact]
    public void WebsiteLinkOpensTheProject() => Sta.Run(() =>
    {
        var (tray, form, prompts) = Open();
        using var _ = tray;
        form.Tabs.SelectedIndex = form.Tabs.TabPages.Count - 1;
        var link = form.Find<LinkLabel>("Website");
        typeof(LinkLabel).GetMethod("OnLinkClicked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(link, [new LinkLabelLinkClickedEventArgs(link.Links[0])]);
        Assert.Equal([UpdateChecker.ProjectUrl], prompts.OpenedUrls);
    });
}

/// <summary>The real dialogs behind <see cref="WinFormsPrompts"/>, answered automatically while they are modal.</summary>
public class WinFormsPromptsTests
{
    [Fact]
    public void ManagedDialogsReturnTheirValues() => Sta.Run(() =>
    {
        using var owner = new Form { ShowInTaskbar = false };
        owner.Show();
        var prompts = WinFormsPrompts.Instance;

        Sta.WhenFormShown<AddDurationDialog>(d => d.DialogResult = DialogResult.OK);
        Assert.Equal(new ActivationDuration(45 * 60), prompts.AskDuration(owner));
        Sta.WhenFormShown<AddDurationDialog>(d => d.DialogResult = DialogResult.Cancel);
        Assert.Null(prompts.AskDuration(owner));

        Sta.WhenFormShown<ScheduleWindowDialog>(d => d.DialogResult = DialogResult.OK);
        Assert.Equal(5, prompts.EditScheduleWindow(owner, null)!.Weekdays.Count);

        Sta.WhenFormShown<TextPromptDialog>(d =>
        {
            DialogTests.AllControls(d).OfType<TextBox>().Single().Text = "Lab";
            d.DialogResult = DialogResult.OK;
        });
        Assert.Equal("Lab", prompts.AskText(owner, "Title", "Label"));

        Sta.WhenFormShown<RunningAppDialog>(d =>
        {
            DialogTests.AllControls(d).OfType<ListBox>().Single().SelectedIndex = 0;
            d.DialogResult = DialogResult.OK;
        });
        Assert.EndsWith(".exe", prompts.ChooseRunningApplication(owner));
    });

    private static void WithOwner(Action<Form> body) => Sta.Run(() =>
    {
        using var owner = new Form { ShowInTaskbar = false };
        owner.Show();
        body(owner);
    });

    [Fact]
    public void MessageBoxesReturnTheButtonPressed() => WithOwner(owner =>
    {
        var prompts = WinFormsPrompts.Instance;
        Sta.WhenNativeDialogShown(Sta.IdOk);
        Assert.True(prompts.Confirm(owner, "Sure?", "Title"));
        Sta.WhenNativeDialogShown(Sta.IdCancel);
        Assert.False(prompts.Confirm(owner, "Sure?", "Title"));
        Sta.WhenNativeDialogShown(Sta.IdOk);
        prompts.Inform(owner, "Done", "Title", MessageBoxIcon.Information);
    });

    [Fact]
    public void CancelledFilePickerReturnsNull() => WithOwner(owner =>
    {
        Sta.WhenNativeDialogShown(Sta.IdCancel);
        Assert.Null(WinFormsPrompts.Instance.ChooseFile(owner, "Title", "Programs (*.exe)|*.exe", Path.GetTempPath()));
    });

    [Fact]
    public void CancelledFolderPickerReturnsNull() => WithOwner(owner =>
    {
        Sta.WhenNativeDialogShown(Sta.IdCancel);
        Assert.Null(WinFormsPrompts.Instance.ChooseFolder(owner, "Choose", Path.GetTempPath()));
    });
}

/// <summary>The Wi-Fi and audio probes' logic over scripted native results.</summary>
public class ProbeLogicTests
{
    private sealed class FakeWlan : IWlanNative
    {
        public bool Opens = true;
        public bool ThrowsDllNotFound;
        public byte[]? Interfaces;
        public Dictionary<Guid, (int, byte[]?)> Connections = [];
        public bool Closed;

        public bool TryOpen(out IntPtr client)
        {
            if (ThrowsDllNotFound) { throw new DllNotFoundException("wlanapi.dll"); }
            client = new IntPtr(42);
            return Opens;
        }

        public void Close(IntPtr client) => Closed = true;
        public byte[]? EnumerateInterfaces(IntPtr client) => Interfaces;
        public (int Result, byte[]? Attributes) QueryCurrentConnection(IntPtr client, Guid interfaceGuid) => Connections[interfaceGuid];
    }

    private static byte[] InterfaceList(params (Guid Id, int State)[] interfaces)
    {
        var buffer = new byte[WindowsDecoding.WlanInterfaceListHeaderSize + interfaces.Length * WindowsDecoding.WlanInterfaceInfoSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), interfaces.Length);
        for (var i = 0; i < interfaces.Length; i++)
        {
            var info = buffer.AsSpan(WindowsDecoding.WlanInterfaceListHeaderSize + i * WindowsDecoding.WlanInterfaceInfoSize);
            interfaces[i].Id.TryWriteBytes(info);
            BitConverter.TryWriteBytes(info.Slice(16 + 512, 4), interfaces[i].State);
        }
        return buffer;
    }

    private static byte[] Attributes(string ssid)
    {
        var buffer = new byte[WindowsDecoding.WlanConnectionSsidOffset + 4 + 32];
        var bytes = System.Text.Encoding.UTF8.GetBytes(ssid);
        BitConverter.TryWriteBytes(buffer.AsSpan(WindowsDecoding.WlanConnectionSsidOffset, 4), bytes.Length);
        bytes.CopyTo(buffer, WindowsDecoding.WlanConnectionSsidOffset + 4);
        return buffer;
    }

    [Fact]
    public void WiFiReadingCoversEveryOutcome()
    {
        var connected = Guid.NewGuid();
        var second = Guid.NewGuid();
        var idle = Guid.NewGuid();
        var wlan = new FakeWlan
        {
            Interfaces = InterfaceList((connected, 1), (second, 1), (idle, 4)),
            Connections = { [connected] = (0, Attributes("Office")), [second] = (0, Attributes("Lab")) },
        };
        var probe = new WlanWiFiProbe(wlan);
        Assert.Equal(new[] { "Office", "Lab" }, probe.ReadNow().Ssids);
        Assert.Equal(["Office", "Lab"], probe.CurrentSsids());
        Assert.True(wlan.Closed);

        wlan.Connections[connected] = (NativeMethods.ErrorAccessDenied, null);
        wlan.Connections[second] = (0, Attributes("")); // redacted
        Assert.Equal(WiFiReadStatus.AccessDenied, probe.ReadNow().Status);

        wlan.Interfaces = InterfaceList((idle, 4));
        Assert.Equal(WiFiReadStatus.NotConnected, probe.ReadNow().Status);

        wlan.Interfaces = InterfaceList();
        Assert.Equal(WiFiReadStatus.Unavailable, probe.ReadNow().Status);
        wlan.Interfaces = null;
        Assert.Equal(WiFiReadStatus.Unavailable, probe.ReadNow().Status);

        wlan.Opens = false;
        Assert.Equal(WiFiReadStatus.Unavailable, probe.ReadNow().Status);
        wlan.ThrowsDllNotFound = true;
        Assert.Equal(WiFiReadStatus.Unavailable, probe.ReadNow().Status);
    }

    [Fact]
    public void QueryFailureOtherThanDeniedMeansNotConnected()
    {
        var id = Guid.NewGuid();
        var probe = new WlanWiFiProbe(new FakeWlan
        {
            Interfaces = InterfaceList((id, 1)),
            Connections = { [id] = (5023 /* ERROR_INVALID_STATE */, null) },
        });
        Assert.Equal(WiFiReadStatus.NotConnected, probe.ReadNow().Status);
    }

    private sealed class FakeAudio(Func<AudioEndpointInfo?> read) : IAudioEndpointReader
    {
        public AudioEndpointInfo? ReadDefaultPlayback() => read();
    }

    [Fact]
    public void AudioProbeClassifiesAndHandlesFailures()
    {
        Assert.True(new CoreAudioProbe(new FakeAudio(() => new AudioEndpointInfo("BTHENUM", 3))).IsDefaultOutputExternal());
        Assert.False(new CoreAudioProbe(new FakeAudio(() => new AudioEndpointInfo("HDAUDIO", 1))).IsDefaultOutputExternal());
        Assert.False(new CoreAudioProbe(new FakeAudio(() => null)).IsDefaultOutputExternal()); // no playback device
        Assert.Null(new CoreAudioProbe(new FakeAudio(() => throw new COMException("busy", -1))).IsDefaultOutputExternal());
    }

    [Fact]
    public void RealAudioReaderAnswersOrThrowsComException()
    {
        try
        {
            var endpoint = new CoreAudioEndpointReader().ReadDefaultPlayback(); // null on machines without speakers
            if (endpoint is not null) { Assert.True(endpoint.EnumeratorName is not null || endpoint.FormFactor is not null); }
        }
        catch (COMException) { /* audio service stopped: the probe maps this to "unknown" */ }
    }
}
