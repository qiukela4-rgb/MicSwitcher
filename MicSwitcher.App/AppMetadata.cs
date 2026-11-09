namespace MicSwitcher.App;

/// <summary>
/// 应用元信息（作者 / 仓库 / 链接）。开源发布时改这里即可，界面和更新检查都读它。
/// </summary>
internal static class AppMetadata
{
    public const string AppName = "MicSwitcher";
    public const string AppDescription = "Windows 麦克风切换工具 · 手动 / 自动 / 锁定，托盘常驻";

    // ---- 开源发布：把 OWNER 改成你的 GitHub 用户名 ----
    public const string GitHubOwner = "qiukela4-rgb";
    public const string GitHubRepo = "MicSwitcher";

    // ---- 作者与协议 ----
    public const string Author = "nian";
    public const string Copyright = "© 2026 nian · MIT License";

    // ---- 打赏（留空则按钮提示“作者还没设置”）----
    public const string DonateUrl = "";
    public const string DonateText = "请作者喝杯咖啡";

    public static string RepoUrl => $"https://github.com/{GitHubOwner}/{GitHubRepo}";
    public static string ReleasesUrl => $"{RepoUrl}/releases";
    public static string IssuesUrl => $"{RepoUrl}/issues";
    public static string StarUrl => $"{RepoUrl}/stargazers";

    /// <summary>当前版本号，形如 1.0.0。</summary>
    public static string Version
    {
        get
        {
            var v = typeof(AppMetadata).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static Version CurrentVersion => typeof(AppMetadata).Assembly.GetName().Version ?? new Version(1, 0, 0);
}
