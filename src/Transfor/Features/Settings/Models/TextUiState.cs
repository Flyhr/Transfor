namespace Transfor;

internal sealed record TextUiState(TextToolId LastViewedTool)
{
    public static TextUiState Default => new(TextToolId.QuoteConversion);
    public void Validate()
    {
        if (!Enum.IsDefined(LastViewedTool)) throw new ArgumentOutOfRangeException(nameof(LastViewedTool));
    }
}