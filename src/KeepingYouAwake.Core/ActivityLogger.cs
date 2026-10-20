using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KeepingYouAwake.Core;

/// <summary>Values of the activity log's <c>source</c> field.</summary>
public static class ActivityLogSources
{
    public const string User = "user";
    public const string WatchedApp = "watched-app";
    public const string WatchedSsid = "watched-ssid";
    public const string ACPower = "ac-power";
    public const string ExternalDisplay = "external-display";
    public const string Schedule = "schedule";
    public const string Download = "download";
    public const string AudioOutput = "audio-output";
    public const string CpuLoad = "cpu-load";
}

/// <summary>Values of the activity log's <c>endedReason</c> field.</summary>
public static class ActivityLogEndedReasons
{
    /// <summary>The session reached its end time.</summary>
    public const string Expired = "expired";
    /// <summary>Tray click, menu, URL scheme, or a battery rule.</summary>
    public const string UserCancelled = "user-cancelled";
    /// <summary>A feature trigger ended the session it started.</summary>
    public const string TriggerCancelled = "trigger-cancelled";
    /// <summary>The app quit (or crashed) while the session was open.</summary>
    public const string AppTerminated = "app-terminated";
}

public sealed record ActivityLogEntry(
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Source,
    double RequestedDuration,
    string? EndedReason);

/// <summary>
/// Append-only JSONL log of sessions, capped to <see cref="MaximumEntries"/> lines,
/// at <c>%APPDATA%\KeepingYouAwake\activity.jsonl</c>. The format matches the macOS
/// app's, so the <c>kya</c> CLI and MCP server read it unchanged. Thread-safe.
/// </summary>
public sealed class ActivityLogger
{
    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> now;

    /// <summary>Line index of the open entry, or -1 when no session is open.</summary>
    private int openLine = -1;
    /// <summary>Set when a dangling-entry repair couldn't be written; the next append retries it.</summary>
    private string? pendingRepairReason;
    /// <summary>End of the open entry whose write failed; the next append records it.</summary>
    private DateTimeOffset? pendingEndedAt;
    private string? pendingEndedReason;

    public ActivityLogger(string filePath, int maximumEntries = 1000, Func<DateTimeOffset>? clock = null)
    {
        FilePath = filePath;
        MaximumEntries = maximumEntries;
        now = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static string DefaultFilePath => Path.Combine(AppPaths.DataDirectory, "activity.jsonl");

    public string FilePath { get; }
    public int MaximumEntries { get; }

    public void RecordStarted(string source, double requestedDuration)
    {
        lock (gate)
        {
            var startedAt = now();
            var lines = ReadAll();
            if (pendingRepairReason is { } repair) { CloseDangling(lines, startedAt, repair); }

            // A session still open here ended without being recorded (its end write failed).
            if (openLine >= 0 && openLine < lines.Count && lines[openLine]["endedAt"] is null)
            {
                lines[openLine]["endedAt"] = Format(pendingEndedAt ?? startedAt);
                lines[openLine]["endedReason"] = pendingEndedReason ?? ActivityLogEndedReasons.Expired;
            }

            lines.Add(new JsonObject
            {
                ["startedAt"] = Format(startedAt),
                ["source"] = source,
                ["requestedDuration"] = requestedDuration,
            });
            if (lines.Count > MaximumEntries) { lines.RemoveRange(0, lines.Count - MaximumEntries); }

            if (WriteAll(lines))
            {
                openLine = lines.Count - 1;
                pendingRepairReason = null;
                pendingEndedAt = null;
                pendingEndedReason = null;
            }
        }
    }

    /// <summary>Closes the open entry. No-op when no session is open.</summary>
    public void RecordEnded(string reason)
    {
        lock (gate)
        {
            if (openLine < 0) { return; }
            var endedAt = now();
            var lines = ReadAll();
            if (openLine >= lines.Count)
            {
                openLine = -1; // trimmed away by the cap
                return;
            }
            lines[openLine]["endedAt"] = Format(endedAt);
            lines[openLine]["endedReason"] = reason;
            if (WriteAll(lines))
            {
                openLine = -1;
                pendingEndedAt = null;
                pendingEndedReason = null;
            }
            else
            {
                pendingEndedAt = endedAt;
                pendingEndedReason = reason;
            }
        }
    }

    /// <summary>
    /// Closes entries left open by a previous process that crashed or was killed, with
    /// <c>endedAt</c> = now (the real end is unknown). Call once at launch.
    /// </summary>
    public void CloseDanglingEntries(string reason)
    {
        lock (gate)
        {
            var lines = ReadAll();
            var changed = CloseDangling(lines, now(), reason);
            pendingRepairReason = changed && !WriteAll(lines) ? reason : null;
            openLine = -1;
        }
    }

    /// <summary>The last <paramref name="count"/> entries, newest first.</summary>
    public IReadOnlyList<ActivityLogEntry> RecentEntries(int count)
    {
        lock (gate)
        {
            var entries = ReadAll().Select(Parse).OfType<ActivityLogEntry>().ToList();
            return entries.Skip(Math.Max(0, entries.Count - count)).Reverse().ToList();
        }
    }

    private static bool CloseDangling(List<JsonObject> lines, DateTimeOffset endedAt, string reason)
    {
        var changed = false;
        foreach (var line in lines.Where(l => l["endedAt"] is null))
        {
            line["endedAt"] = Format(endedAt);
            if (reason.Length > 0) { line["endedReason"] = reason; }
            changed = true;
        }
        return changed;
    }

    private List<JsonObject> ReadAll()
    {
        var result = new List<JsonObject>();
        string[] raw;
        try { raw = File.ReadAllLines(FilePath, Encoding.UTF8); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return result; }
        foreach (var line in raw)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            try
            {
                if (JsonNode.Parse(line) is JsonObject obj) { result.Add(obj); }
            }
            catch (JsonException) { /* skip a corrupt line */ }
        }
        return result;
    }

    private bool WriteAll(List<JsonObject> lines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var builder = new StringBuilder();
            foreach (var line in lines) { builder.Append(line.ToJsonString()).Append('\n'); }
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string Format(DateTimeOffset date) => date.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

    internal static ActivityLogEntry? Parse(JsonObject obj)
    {
        if (!TryDate(obj["startedAt"], out var startedAt)) { return null; }
        DateTimeOffset? endedAt = TryDate(obj["endedAt"], out var e) ? e : null;
        var source = obj["source"] is JsonValue s && s.TryGetValue<string>(out var src) ? src : ActivityLogSources.User;
        double requested = -1;
        if (obj["requestedDuration"] is JsonValue r && r.TryGetValue<double>(out var d)) { requested = d; }
        var reason = obj["endedReason"] is JsonValue rv && rv.TryGetValue<string>(out var rs) ? rs : null;
        return new ActivityLogEntry(startedAt, endedAt, source, requested, reason);
    }

    private static bool TryDate(JsonNode? node, out DateTimeOffset date)
    {
        date = default;
        return node is JsonValue v && v.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }
}
