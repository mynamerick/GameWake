using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GameWake.App.Services;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string Current,
    string? Latest,
    string PageUrl,
    string? Error);

/// <summary>User-initiated GitHub release check. Nothing runs in the background.</summary>
public static class UpdateChecker
{
    public const string RepoUrl = "https://github.com/mynamerick/GameWake";
    public const string ReleasesUrl = "https://github.com/mynamerick/GameWake/releases";
    public const string LatestApiUrl = "https://api.github.com/repos/mynamerick/GameWake/releases/latest";

    public static async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var current = Normalize(currentVersion);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GameWake", current));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await client.GetAsync(LatestApiUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, current, null, ReleasesUrl, $"GitHub returned {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var page = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return new UpdateCheckResult(false, current, null, ReleasesUrl, "GitHub response had no tag");
            }

            var latest = Normalize(tag);
            var newer = Compare(current, latest) < 0;
            return new UpdateCheckResult(newer, current, latest, string.IsNullOrWhiteSpace(page) ? ReleasesUrl : page, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return new UpdateCheckResult(false, current, null, ReleasesUrl, ex.Message);
        }
    }

    public static int Compare(string current, string latest) => Parse(current).CompareTo(Parse(latest));

    public static string Normalize(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        return string.IsNullOrWhiteSpace(value) ? "0.0.0" : value;
    }

    private static Version Parse(string version)
    {
        var value = Normalize(version);
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            value = value[..dash];
        }

        return Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0, 0);
    }
}
