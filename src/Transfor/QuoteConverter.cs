namespace Transfor;

public static class QuoteConverter
{
    public static string Convert(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace('"', '\'')
            .Replace('\u201C', '\u2018')
            .Replace('\u201D', '\u2019');
    }
}
