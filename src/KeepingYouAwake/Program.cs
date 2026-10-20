using KeepingYouAwake.Core;
using KeepingYouAwake.UI;
using KeepingYouAwake.Windows;

namespace KeepingYouAwake;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = SingleInstance.Acquire(args);
        if (instance is null) { return 0; } // forwarded to the running instance

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception);

        ShellIntegration.RegisterUrlScheme();
        ShellIntegration.RefreshStartAtLoginPath();

        var store = new SettingsStore(SettingsStore.DefaultFilePath);
        var log = new ActivityLogger(ActivityLogger.DefaultFilePath);

        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        UiThread.Capture();

        using var context = new TrayApplicationContext(store, log);
        instance.Listen(context.Handle);
        if (args.Length > 0) { context.Handle(args); }
        Application.Run(context);
        return 0;
    }

    private static void ReportError(Exception exception)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "error.log");
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {exception}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
