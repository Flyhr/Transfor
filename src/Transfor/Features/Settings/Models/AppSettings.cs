namespace Transfor;

internal sealed record AppSettings(
    HotKeyBinding HistoryHotKey,
    int QuoteHistoryLimit,
    int SpaceHistoryLimit,
    TextToolId LastViewedTool)
{
    public const int MinimumHistoryLimit = 1;
    public const int MaximumHistoryLimit = 500;

    public static AppSettings Default => new(
        HotKeyBinding.Default,
        100,
        100,
        TextToolId.QuoteConversion);

    public void Validate()
    {
        if (HistoryHotKey is null)
        {
            throw new ArgumentException("必须设置历史面板快捷键。", nameof(HistoryHotKey));
        }

        HotKeyBinding.Create(HistoryHotKey.Modifiers, HistoryHotKey.Key);
        ValidateHistoryLimit(QuoteHistoryLimit, nameof(QuoteHistoryLimit));
        ValidateHistoryLimit(SpaceHistoryLimit, nameof(SpaceHistoryLimit));
        if (!Enum.IsDefined(LastViewedTool))
        {
            throw new ArgumentException("最后查看的功能无效。", nameof(LastViewedTool));
        }
    }

    public static void ValidateHistoryLimit(int value, string parameterName)
    {
        if (value is < MinimumHistoryLimit or > MaximumHistoryLimit)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "历史上限必须在 1 到 500 之间。");
        }
    }
}