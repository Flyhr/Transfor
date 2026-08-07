namespace Transfor;

// 版本比较入口：非法版本抛 FormatException（由 UpdateService 转为 CheckFailed，绝不锁死应用）
internal static class VersionComparer
{
    public static int Compare(string? left, string? right)
    {
        if (!UpdateVersion.TryParse(left, out var leftVersion) || !UpdateVersion.TryParse(right, out var rightVersion))
        {
            throw new FormatException($"无法解析版本号：'{left ?? "(空)"}' vs '{right ?? "(空)"}'");
        }

        return leftVersion!.CompareTo(rightVersion);
    }
}
