namespace Transfor;

// 应用设置：历史面板全局快捷键 + 两种转换功能各自的历史上限
internal sealed record AppSettings(HotKeyBinding HistoryHotKey, int QuoteHistoryLimit, int SpaceHistoryLimit)
{
    public const int MinimumHistoryLimit = 1;
    public const int MaximumHistoryLimit = 500;

    // 默认设置：快捷键 Alt+Q，两种历史各保留 100 条
    public static AppSettings Default => new(HotKeyBinding.Default, 100, 100);

    // 校验设置合法性，非法时抛出异常
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(HistoryHotKey);
        HotKeyBinding.Create(HistoryHotKey.Modifiers, HistoryHotKey.Key);
        ValidateHistoryLimit(QuoteHistoryLimit, nameof(QuoteHistoryLimit));
        ValidateHistoryLimit(SpaceHistoryLimit, nameof(SpaceHistoryLimit));
    }

    // 校验历史上限必须在 1–500 之间
    public static void ValidateHistoryLimit(int value, string parameterName)
    {
        if (value is < MinimumHistoryLimit or > MaximumHistoryLimit) throw new ArgumentOutOfRangeException(parameterName, value, "历史上限必须在 1 到 500 之间。");
    }
}
