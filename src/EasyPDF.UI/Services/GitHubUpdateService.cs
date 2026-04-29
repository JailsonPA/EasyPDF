using EasyPDF.Application.Interfaces;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace EasyPDF.UI.Services;

/// <summary>
/// Checks the GitHub Releases API and returns an <see cref="UpdateInfo"/> if
/// a version newer than the running assembly is available.
/// All errors (network, parse, rate-limit) are silently swallowed so a failed
/// check never surfaces to the user.
/// </summary>
internal sealed class GitHubUpdateService : IUpdateService
{
    private static readonly HttpClient _http = CreateClient();

    private readonly string _owner;
    private readonly string _repo;

    public GitHubUpdateService(string owner, string repo)
    {
        _owner = owner;
        _repo  = repo;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl) ||
                !root.TryGetProperty("html_url", out var urlEl)) return null;

            string tag        = tagEl.GetString() ?? "";
            string releaseUrl = urlEl.GetString() ?? "";

            // Tags are typically "v1.2.3" — strip the leading 'v' before parsing.
            string versionStr = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionStr, out var releaseVersion)) return null;

            var current = GetCurrentVersion();
            return releaseVersion > current ? new UpdateInfo(versionStr, releaseUrl) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Version GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    private static HttpClient CreateClient()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        string ua = v is null ? "EasyPDF/1.0" : $"EasyPDF/{v.Major}.{v.Minor}.{v.Build}";

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.Add("User-Agent", ua);
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
