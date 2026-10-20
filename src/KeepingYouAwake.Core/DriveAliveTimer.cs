using System.Text;
using KeepingYouAwake.Core.Platform;

namespace KeepingYouAwake.Core;

/// <summary>
/// Keeps disks from spinning down by rewriting a small file on them every
/// <see cref="Interval"/> while a session runs: a temp file on the system drive
/// (Prevent disk sleep) and, with <c>includeExternalDrives</c>, a hidden
/// <c>.KeepingYouAwake-DriveAlive</c> file at the root of every other local,
/// writable drive (Drive Alive). Files are removed when the session ends.
/// </summary>
/// <remarks>
/// A file of that name that isn't ours (non-empty without our marker), a directory
/// or a reparse point is never written or removed. Disk I/O runs off the UI thread,
/// in order, because a sleeping drive can take seconds to spin up.
/// </remarks>
public sealed class DriveAliveTimer : IDisposable
{
    public const string PingFileName = ".KeepingYouAwake-DriveAlive";
    internal const string ContentPrefix = "keepingyouawake ";

    private readonly IScheduler scheduler;
    private readonly IDriveEnumerator? drives;
    private readonly string systemPingPath;
    private readonly string token = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> writtenPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object ioLock = new();
    private readonly bool runInline;
    private Task ioChain = Task.CompletedTask;
    private IDisposable? tick;

    /// <param name="drives">Source of external drive roots, or null to only keep the system drive awake.</param>
    /// <param name="systemPingDirectory">Directory on the system drive for its ping file (default: the temp folder).</param>
    /// <param name="runInline">Run disk I/O synchronously (tests).</param>
    public DriveAliveTimer(IScheduler scheduler, IDriveEnumerator? drives, string? systemPingDirectory = null, bool runInline = false)
    {
        this.scheduler = scheduler;
        this.drives = drives;
        this.runInline = runInline;
        systemPingPath = Path.Combine(systemPingDirectory ?? Path.GetTempPath(),
            $"KeepingYouAwake.drive-alive.{Environment.ProcessId}");
    }

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);
    public bool IsRunning => tick is not null;
    public bool IncludesExternalDrives => drives is not null;

    public void Start()
    {
        if (IsRunning) { return; }
        tick = scheduler.Every(Interval, Touch);
        Touch();
    }

    public void Stop()
    {
        if (!IsRunning) { return; }
        tick!.Dispose();
        tick = null;
        // Queued after every pending write, so a removal never races a write.
        Enqueue(RemoveAll);
    }

    public void Dispose() => Stop();

    /// <summary>Waits for queued disk I/O (tests, app exit).</summary>
    public void Flush(TimeSpan timeout)
    {
        Task chain;
        lock (ioLock) { chain = ioChain; }
        chain.Wait(timeout);
    }

    private void Touch()
    {
        var roots = drives?.ExternalWritableRoots() ?? [];
        Enqueue(() =>
        {
            var payload = Encoding.UTF8.GetBytes($"{ContentPrefix}{token} {DateTimeOffset.UtcNow:O}\n");
            if (WritePing(systemPingPath, payload, hidden: false)) { writtenPaths.Add(systemPingPath); }
            foreach (var root in roots)
            {
                var path = Path.Combine(root, PingFileName);
                if (WritePing(path, payload, hidden: true)) { writtenPaths.Add(path); }
            }
        });
    }

    private void RemoveAll()
    {
        var ownPrefix = $"{ContentPrefix}{token} ";
        foreach (var path in writtenPaths)
        {
            try
            {
                if (ReadHead(path) is { } head && head.StartsWith(ownPrefix, StringComparison.Ordinal))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch (Exception e) when (IsIOError(e)) { /* best effort: the drive may be gone */ }
        }
        writtenPaths.Clear();
    }

    private void Enqueue(Action work)
    {
        if (runInline)
        {
            lock (ioLock) { work(); }
            return;
        }
        lock (ioLock)
        {
            ioChain = ioChain.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception e) when (IsIOError(e)) { }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Writes the ping unless the path holds something that isn't ours.</summary>
    internal static bool WritePing(string path, byte[] payload, bool hidden)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var head = ReadHead(path);
                // Reuse ours from an earlier (or crashed) session and empty files a failed write left behind.
                if (head is null || (head.Length > 0 && !head.StartsWith(ContentPrefix, StringComparison.Ordinal))) { return false; }
                File.SetAttributes(path, FileAttributes.Normal);
            }
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try { stream.Write(payload); }
                catch (IOException)
                {
                    // Leave an empty file, which the next tick reclaims, not a partial one without the marker.
                    stream.SetLength(0);
                    throw;
                }
            }
            if (hidden && OperatingSystem.IsWindows())
            {
                File.SetAttributes(path, FileAttributes.Hidden);
            }
            return true;
        }
        catch (Exception e) when (IsIOError(e))
        {
            return false;
        }
    }

    /// <summary>The first bytes of a regular file; null for directories, reparse points and unreadable paths.</summary>
    internal static string? ReadHead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { return null; }
            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) { return null; }
            if (info.LinkTarget is not null) { return null; }
            if (info.Length == 0) { return string.Empty; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[128];
            var count = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, count);
        }
        catch (Exception e) when (IsIOError(e))
        {
            return null;
        }
    }

    private static bool IsIOError(Exception e) =>
        e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
}
