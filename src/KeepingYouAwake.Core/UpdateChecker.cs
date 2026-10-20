using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KeepingYouAwake.Core;

public sealed record ReleaseInfo(string Tag, Version Version, string? PreReleaseLabel, string PageUrl, bool IsPreRelease);

/// <summary>Checks GitHub Releases for a newer version.</summary>
public sealed class UpdateChecker(HttpClient http, string repository = UpdateChecker.DefaultRepository)
{
    /// <summary><c>owner/name</c> of the GitHub repository that publishes releases.</summary>
    public const string DefaultRepository = "ryouka0731/KeepingYouAwakeForWin";

    public static string ProjectUrl => $"https://github.com/{DefaultRepository}";

    /// <summary>The newest release newer than <paramref name="current"/>, or null when up to date.</summary>
    public async Task<ReleaseInfo?> FindUpdateAsync(string current, bool includePreReleases, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("KeepingYouAwake", ParseVersion(current)?.Version.ToString(3) ?? "0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return SelectUpdate(json, current, includePreReleases);
    }

    /// <summary>Picks the newest eligible release from a GitHub <c>/releases</c> response.</summary>
    public static ReleaseInfo? SelectUpdate(string releasesJson, string current, bool includePreReleases)
    {
        var currentVersion = ParseVersion(current);
        if (currentVersion is null) { return null; }

        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) { return null; }

        var best = document.RootElement.EnumerateArray()
            .Select(ParseRelease)
            .OfType<ReleaseInfo>()
            .Where(r => includePreReleases || !r.IsPreRelease)
            .Aggregate(default(ReleaseInfo), (newest, r) => newest is null || IsNewer(r, newest) ? r : newest);

        if (best is null) { return null; }
        return Compare(best.Version, best.PreReleaseLabel, currentVersion.Value.Version, currentVersion.Value.Label) > 0 ? best : null;
    }

    /// <summary>A published release from the API, or null for drafts and tags that aren't versions.</summary>
    internal static ReleaseInfo? ParseRelease(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) { return null; }
        if (IsTrue(release, "draft")) { return null; }
        if (!release.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String) { return null; }
        var tag = tagElement.GetString()!;
        if (ParseVersion(tag) is not { } parsed) { return null; }
        var url = release.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : string.Empty;
        var preRelease = IsTrue(release, "prerelease") || parsed.Label is not null;
        return new ReleaseInfo(tag, parsed.Version, parsed.Label, url, preRelease);
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool IsNewer(ReleaseInfo a, ReleaseInfo b) =>
        Compare(a.Version, a.PreReleaseLabel, b.Version, b.PreReleaseLabel) > 0;

    /// <summary>Parses <c>v1.2.3</c>, <c>1.2</c>, <c>1.2.3-beta.1</c>. Build metadata (<c>+…</c>) is ignored.</summary>
    public static (Version Version, string? Label)? ParseVersion(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) { value = value[1..]; }
        var plus = value.IndexOf('+');
        if (plus >= 0) { value = value[..plus]; }
        string? label = null;
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            label = value[(dash + 1)..];
            value = value[..dash];
            if (label.Length == 0) { label = null; }
        }
        if (!Version.TryParse(value.Contains('.') ? value : value + ".0", out var version)) { return null; }
        return (Normalize(version), label);
    }

    /// <summary>SemVer precedence: a release outranks its own pre-releases; labels compare per dot-separated part.</summary>
    public static int Compare(Version a, string? aLabel, Version b, string? bLabel)
    {
        var byVersion = Normalize(a).CompareTo(Normalize(b));
        if (byVersion != 0) { return byVersion; }
        if (aLabel is null && bLabel is null) { return 0; }
        if (aLabel is null) { return 1; }
        if (bLabel is null) { return -1; }

        var aParts = aLabel.Split('.');
        var bParts = bLabel.Split('.');
        for (var i = 0; i < Math.Min(aParts.Length, bParts.Length); i++)
        {
            var aNumeric = long.TryParse(aParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var an);
            var bNumeric = long.TryParse(bParts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
            var cmp = (aNumeric, bNumeric) switch
            {
                (true, true) => an.CompareTo(bn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(aParts[i], bParts[i]),
            };
            if (cmp != 0) { return Math.Sign(cmp); }
        }
        return aParts.Length.CompareTo(bParts.Length);
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
