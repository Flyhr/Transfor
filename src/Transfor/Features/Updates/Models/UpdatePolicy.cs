namespace Transfor;

// 远程更新策略（update-policy.json）：enabled 开关、版本区间、通道、
// 发布说明与下载地址；字段与计划文档 JSON 样例一致
internal sealed class UpdatePolicy
{
    public bool Enabled { get; set; } = true;

    public string? LatestVersion { get; set; }

    public string? MinimumVersion { get; set; }

    public string? Channel { get; set; }

    public DateTimeOffset? ReleaseDate { get; set; }

    public string? Title { get; set; }

    public string? Message { get; set; }

    public List<string>? Changelog { get; set; }

    public string? DownloadUrl { get; set; }

    public string? Sha256 { get; set; }

    // 策略所属通道：channel 缺省或未知值视为 stable
    public UpdateChannel ChannelKind => string.Equals(Channel, "beta", StringComparison.OrdinalIgnoreCase)
        ? UpdateChannel.Beta
        : UpdateChannel.Stable;
}
