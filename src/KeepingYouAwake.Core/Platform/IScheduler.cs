namespace KeepingYouAwake.Core.Platform;

/// <summary>
/// Clock and timer source for everything in Core. The app backs it with
/// UI-thread timers, so every callback runs on the UI thread and Core needs
/// no locking; tests back it with a manually advanced fake.
/// </summary>
public interface IScheduler
{
    DateTimeOffset Now { get; }

    /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>. Dispose to cancel.</summary>
    IDisposable After(TimeSpan delay, Action action);

    /// <summary>Runs <paramref name="action"/> every <paramref name="interval"/>. Dispose to stop.</summary>
    IDisposable Every(TimeSpan interval, Action action);
}
