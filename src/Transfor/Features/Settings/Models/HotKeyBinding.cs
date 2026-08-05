using System.Windows.Forms;

namespace Transfor;

// 全局快捷键绑定：修饰键 + 普通按键，提供合法性校验、可读文本与 Win32 注册所需的格式
internal sealed record HotKeyBinding
{
    // 允许作为修饰键的按键集合
    private const Keys AllowedModifiers = Keys.Control | Keys.Alt | Keys.Shift | Keys.LWin | Keys.RWin;

    private HotKeyBinding(Keys modifiers, Keys key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public Keys Modifiers { get; }

    public Keys Key { get; }

    // 默认快捷键：Alt+Q
    public static HotKeyBinding Default => Create(Keys.Alt, Keys.Q);

    // 可读文本，例如 "Alt+Q"、"Ctrl+Shift+F5"
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin)) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join("+", parts);
        }
    }

    // 创建绑定并校验合法性：必须包含至少一个修饰键和一个普通按键
    public static HotKeyBinding Create(Keys modifiers, Keys key)
    {
        // 过滤掉允许范围外的修饰位；缺少修饰键或混入非法位时拒绝
        var normalizedModifiers = modifiers & AllowedModifiers;
        if (normalizedModifiers == Keys.None || (modifiers & ~AllowedModifiers) != 0)
        {
            throw new ArgumentException("快捷键至少需要一个 Ctrl、Alt、Shift 或 Win 修饰键。", nameof(modifiers));
        }

        // 主键必须是普通按键（排除修饰键本身、保留键位以外的非法值）
        var keyCode = key & Keys.KeyCode;
        if (key == Keys.None || keyCode is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin || keyCode < Keys.Back || (key & Keys.Modifiers) != 0 || keyCode == Keys.None)
        {
            throw new ArgumentException("快捷键必须包含一个普通按键。", nameof(key));
        }

        return new HotKeyBinding(normalizedModifiers, key & Keys.KeyCode);
    }

    // 转换为 RegisterHotKey 所需的修饰键标志位（MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN）
    public int ToNativeModifiers()
    {
        var value = 0;
        if (Modifiers.HasFlag(Keys.Alt)) value |= 0x0001;
        if (Modifiers.HasFlag(Keys.Control)) value |= 0x0002;
        if (Modifiers.HasFlag(Keys.Shift)) value |= 0x0004;
        if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin)) value |= 0x0008;
        return value;
    }

    public override string ToString() => DisplayText;
}
