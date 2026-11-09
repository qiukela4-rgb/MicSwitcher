using System.Net.Http;
using System.Text.Json;

namespace MicSwitcher.Core.Updates;

/// <summary>一次更新检查的结果。</summary>
public sealed record UpdateCheckResult(
    string LatestTag,
    string ReleaseUrl,
    string? Notes,
    bool HasUpdate);

/// <summary>
/// 从 GitHub Releases 检查更新（只用公开 API，不需要 token）。
/// 仓库还没有发布任何 Release 时返回 null。
/// </summary>
public static class UpdateChecker
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MicSwitcher-UpdateCheck");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 查询最新 Release 并与当前版本比较。
    /// 返回 null 表示查不到（网络异常 / 仓库没有 Release / 返回格式不认识）。
    /// </summary>
    public static async Task<UpdateCheckResult?> CheckAsync(
        string owner, string repo, Version current, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;

        try
        {
            string url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            string json = await Http.GetStringAsync(url, ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            string? notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var latest = ParseTag(tag);
            bool hasUpdate = latest is not null && latest > Normalize(current);

            return new UpdateCheckResult(tag, htmlUrl, notes, hasUpdate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 v1.2.3 / 1.2.3 / v1.2 这类标签解析成 Version。</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var text = tag.Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) text = text[..dash];

        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
