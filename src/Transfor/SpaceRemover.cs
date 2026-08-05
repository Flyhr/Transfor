namespace Transfor;

public static class SpaceRemover
{
    public static string Remove(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input
            .Replace(" ", string.Empty)
            .Replace("\u3000", string.Empty);
    }
}
