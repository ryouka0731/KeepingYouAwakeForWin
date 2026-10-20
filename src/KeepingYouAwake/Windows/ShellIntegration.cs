using System.IO.Pipes;
using KeepingYouAwake.Core;
using KeepingYouAwake.Core.Platform;
using Microsoft.Win32;

namespace KeepingYouAwake.Windows;

/// <summary>Per-user registry integration: Start at Login and the <c>keepingyouawake:</c> URL scheme.</summary>
internal sealed class ShellIntegration(RegistryKey root, string executablePath, string keyPrefix = "")
{
    private const string RunValue = "KeepingYouAwake";
    private readonly string runKey = keyPrefix + @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string protocolKey = keyPrefix + @"Software\Classes\" + AppCommand.Scheme;

    /// <summary>HKEY_CURRENT_USER and this executable.</summary>
    public static ShellIntegration Current { get; } =
        new(Registry.CurrentUser, Environment.ProcessPath ?? Application.ExecutablePath);

    public bool IsStartAtLoginEnabled
    {
        get
        {
            using var key = root.OpenSubKey(runKey);
            return key?.GetValue(RunValue) is string value && value.Length > 0;
        }
    }

    public void SetStartAtLogin(bool enabled)
    {
        using var key = root.CreateSubKey(runKey);
        if (enabled) { key.SetValue(RunValue, ShellCommands.StartAtLogin(executablePath)); }
        else { key.DeleteValue(RunValue, throwOnMissingValue: false); }
    }

    /// <summary>Points an existing Start at Login entry at this executable (after the app was moved or updated).</summary>
    public void RefreshStartAtLoginPath()
    {
        if (IsStartAtLoginEnabled) { SetStartAtLogin(true); }
    }

    /// <summary>Registers <c>keepingyouawake:</c> for the current user, pointing at this executable.</summary>
    public bool RegisterUrlScheme()
    {
        try
        {
            using var key = root.CreateSubKey(protocolKey);
            key.SetValue(string.Empty, "URL:KeepingYouAwake");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon")) { icon.SetValue(string.Empty, ShellCommands.Icon(executablePath)); }
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, ShellCommands.OpenUrl(executablePath));
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Policy may block HKCU\Software\Classes; the app still works without the URL scheme.
            return false;
        }
    }

    /// <summary>The registered URL-scheme command, or null.</summary>
    public string? RegisteredUrlCommand()
    {
        using var key = root.OpenSubKey(protocolKey + @"\shell\open\command");
        return key?.GetValue(string.Empty) as string;
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>
/// One instance per user session. Later launches (including URL-scheme launches) hand
/// their arguments to the running instance over a named pipe restricted to the current user.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        this.mutex = mutex;
        this.pipeName = pipeName;
    }

    /// <summary>Unique per user (SID) and Windows session: pipe names are machine-wide, unlike <c>Local\</c> mutexes.</summary>
    internal static string Name
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return $"KeepingYouAwake-{identity.User?.Value ?? Environment.UserName}-{process.SessionId}";
        }
    }

    /// <summary>Returns the instance guard, or null when another instance already runs (arguments were forwarded).</summary>
    public static SingleInstance? Acquire(string[] args) => Acquire(Name, args);

    /// <summary>Same as <see cref="Acquire(string[])"/> with an explicit name (tests).</summary>
    internal static SingleInstance? Acquire(string name, string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + name, out var createdNew);
        if (createdNew) { return new SingleInstance(mutex, name); }

        mutex.Dispose();
        Forward(name, args);
        return null;
    }

    /// <summary>Starts listening; <paramref name="onArguments"/> runs on the UI thread.</summary>
    public void Listen(Action<string[]> onArguments)
    {
        var token = cancellation.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    var buffer = new byte[InstanceMessage.MaximumBytes];
                    var total = 0;
                    int read;
                    while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                    }
                    var lines = InstanceMessage.Decode(buffer.AsSpan(0, total));
                    UiThread.Post(() => onArguments(lines));
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* client went away; keep listening */ }
                catch (UnauthorizedAccessException) { await Task.Delay(1000, token).ConfigureAwait(false); }
            }
        }, token);
    }

    private static void Forward(string pipeName, string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            client.Write(InstanceMessage.Encode(args));
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        cancellation.Cancel();
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
    }
}
