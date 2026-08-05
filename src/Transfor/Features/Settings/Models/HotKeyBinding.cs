using System.Windows.Forms;

namespace Transfor;

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
            if (Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin)) parts.Add("Win");
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
        if (Modifiers.HasFlag(Keys.Alt)) value |= 0x0001;
        if (Modifiers.HasFlag(Keys.Control)) value |= 0x0002;
        if (Modifiers.HasFlag(Keys.Shift)) value |= 0x0004;
        if (Modifiers.HasFlag(Keys.LWin) || Modifiers.HasFlag(Keys.RWin)) value |= 0x0008;
        return value;
    }

    public override string ToString() => DisplayText;
}