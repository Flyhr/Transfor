using System.Windows.Forms;

namespace Transfor;

internal enum ToolId
{
    QuoteConversion,
    SpaceRemoval,
}

internal sealed record HistoryEntry(
    ToolId Tool,
    string OriginalInput,
    string ConvertedOutput,
    DateTimeOffset CreatedAtUtc);

internal sealed record HotKeyBinding
{
    private const Keys AllowedModifiers = Keys.Control | Keys.Alt | Keys.Shift | Keys.LWin | Keys.RWin;

    private HotKeyBinding(Keys modifiers, Keys key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public Keys Modifiers { get; }

    public Keys Key { get; }

    public static HotKeyBinding Default => Create(Keys.Alt, Keys.Q);

    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(Keys.Control))
            {
                parts.Add("Ctrl");
            }

            if (Modifiers.HasFlag(Keys.Alt))
            {
                parts.Add("Alt");
            }

            if (Modifiers.HasFlag(Keys.Shift))
            {
                parts.Add("Shift");
            }

            if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin))
            {
                parts.Add("Win");
            }

            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    public static HotKeyBinding Create(Keys modifiers, Keys key)
    {
        var normalizedModifiers = modifiers & AllowedModifiers;
        if (normalizedModifiers == Keys.None || (modifiers & ~AllowedModifiers) != 0)
        {
            throw new ArgumentException("快捷键至少需要一个 Ctrl、Alt、Shift 或 Win 修饰键。", nameof(modifiers));
        }

        var keyCode = key & Keys.KeyCode;
        if (key == Keys.None || keyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin || keyCode < Keys.Back || (key & Keys.Modifiers) != 0 || keyCode == Keys.None)
        {
            throw new ArgumentException("快捷键必须包含一个普通按键。", nameof(key));
        }

        return new HotKeyBinding(normalizedModifiers, key & Keys.KeyCode);
    }

    public int ToNativeModifiers()
    {
        var value = 0;
        if (Modifiers.HasFlag(Keys.Alt))
        {
            value |= 0x0001;
        }

        if (Modifiers.HasFlag(Keys.Control))
        {
            value |= 0x0002;
        }

        if (Modifiers.HasFlag(Keys.Shift))
        {
            value |= 0x0004;
        }

        if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin))
        {
            value |= 0x0008;
        }

        return value;
    }

    public override string ToString() => DisplayText;
}

internal sealed record AppSettings(
    HotKeyBinding HistoryHotKey,
    int QuoteHistoryLimit,
    int SpaceHistoryLimit,
    ToolId LastViewedTool)
{
    public const int MinimumHistoryLimit = 1;
    public const int MaximumHistoryLimit = 500;

    public static AppSettings Default => new(
        HotKeyBinding.Default,
        100,
        100,
        ToolId.QuoteConversion);

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





