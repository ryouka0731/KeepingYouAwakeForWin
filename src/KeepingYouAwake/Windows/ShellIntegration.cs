using System.IO.Pipes;
using System.Text;
using KeepingYouAwake.Core;
using Microsoft.Win32;

namespace KeepingYouAwake.Windows;

/// <summary>Per-user registry integration: Start at Login and the <c>keepingyouawake:</c> URL scheme.</summary>
internal static class ShellIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "KeepingYouAwake";
    private const string ProtocolKey = @"Software\Classes\" + AppCommand.Scheme;

    public static string ExecutablePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsStartAtLoginEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string value && value.Length > 0;
        }
    }

    public static void SetStartAtLogin(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) { key.SetValue(RunValue, $"\"{ExecutablePath}\""); }
        else { key.DeleteValue(RunValue, throwOnMissingValue: false); }
    }

    /// <summary>Points an existing Start at Login entry at this executable (after the app was moved or updated).</summary>
    public static void RefreshStartAtLoginPath()
    {
        if (IsStartAtLoginEnabled) { SetStartAtLogin(true); }
    }

    /// <summary>Registers <c>keepingyouawake:</c> for the current user, pointing at this executable.</summary>
    public static void RegisterUrlScheme()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
            key.SetValue(string.Empty, "URL:KeepingYouAwake");
            key.SetValue("URL Protocol", string.Empty);
            using (var icon = key.CreateSubKey("DefaultIcon")) { icon.SetValue(string.Empty, $"\"{ExecutablePath}\",0"); }
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{ExecutablePath}\" \"%1\"");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Policy may block HKCU\Software\Classes; the app still works without the URL scheme.
        }
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
    private const int MaxMessageLength = 8 * 1024;
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cancellation = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        this.mutex = mutex;
        this.pipeName = pipeName;
    }

    /// <summary>Unique per user (SID) and Windows session: pipe names are machine-wide, unlike <c>Local\</c> mutexes.</summary>
    private static string Name
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return $"KeepingYouAwake-{identity.User?.Value ?? Environment.UserName}-{process.SessionId}";
        }
    }

    /// <summary>Returns the instance guard, or null when another instance already runs (arguments were forwarded).</summary>
    public static SingleInstance? Acquire(string[] args)
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        if (createdNew) { return new SingleInstance(mutex, Name); }

        mutex.Dispose();
        Forward(Name, args);
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
                    var buffer = new byte[MaxMessageLength];
                    var total = 0;
                    int read;
                    while (total < buffer.Length && (read = await server.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                    }
                    var lines = Encoding.UTF8.GetString(buffer, 0, total)
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? ["--settings"] : args));
            client.Write(payload, 0, Math.Min(payload.Length, MaxMessageLength));
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
