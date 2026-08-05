namespace Transfor;

// 界面状态：记录用户最后查看/使用的文本工具，用于下次打开面板时恢复
internal sealed record TextUiState(TextToolId LastViewedTool)
{
    // 默认界面状态：引号转换
    public static TextUiState Default => new(TextToolId.QuoteConversion);

    // 校验枚举值是否合法
    public void Validate()
    {
        if (!Enum.IsDefined(LastViewedTool)) throw new ArgumentOutOfRangeException(nameof(LastViewedTool));
    }
}
