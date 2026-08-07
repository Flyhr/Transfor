using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Transfor;

// 语义化版本（SemVer 子集）：严格 MAJOR.MINOR.PATCH 三段与可选预发布标签（-beta.1 / -rc.1）；
// 不允许 2 段（"0.9"）或 4 段（程序集号）——策略与标签必须符合 SemVer，避免歧义；
// 预发布排序遵循 SemVer 2.0
internal sealed record UpdateVersion(int Major, int Minor, int Patch, IReadOnlyList<string>? Prerelease)
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out UpdateVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        // 剥离 InformationalVersion 可能携带的 +build 元数据
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var core = dash >= 0 ? value[..dash] : value;
        var prereleaseText = dash >= 0 ? value[(dash + 1)..] : null;

        var parts = core.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return false;
            }
            numbers[i] = n;
        }

        IReadOnlyList<string>? prerelease = null;
        if (!string.IsNullOrEmpty(prereleaseText))
        {
            var identifiers = prereleaseText.Split('.');
            foreach (var identifier in identifiers)
            {
                if (!IsValidPrereleaseIdentifier(identifier))
                {
                    return false;
                }
            }
            prerelease = identifiers;
        }

        version = new UpdateVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // 正式版 > 任何预发布版本
        if (Prerelease is null && other.Prerelease is null)
        {
            return 0;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public override string ToString() =>
        Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{string.Join('.', Prerelease)}";

    // 预发布标识：ASCII 字母/数字/连字符；纯数字不得以 0 开头（除非就是 "0"）
    private static bool IsValidPrereleaseIdentifier(string identifier)
    {
        if (identifier.Length == 0)
        {
            return false;
        }

        var numeric = true;
        foreach (var c in identifier)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-')
            {
                numeric = false;
            }
            else if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (numeric && identifier.Length > 1 && identifier[0] == '0')
        {
            return false;
        }

        return true;
    }

    private static bool IsNumeric(string identifier)
    {
        foreach (var c in identifier)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return identifier.Length > 0;
    }

    private static int ComparePrerelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var count = Math.Min(left.Count, right.Count);
        for (var i = 0; i < count; i++)
        {
            var a = left[i];
            var b = right[i];
            var aNumeric = IsNumeric(a);
            var bNumeric = IsNumeric(b);

            if (aNumeric && bNumeric)
            {
                // 数字标识按数值比较；溢出等极端情况退化为序数比较
                if (long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an)
                    && long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn))
                {
                    var comparison = an.CompareTo(bn);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }
                else
                {
                    var comparison = a.Length.CompareTo(b.Length);
                    if (comparison != 0)
                    {
                        return comparison;
                    }
                }
            }
            else if (aNumeric)
            {
                return -1; // 数字标识 < 字母标识
            }
            else if (bNumeric)
            {
                return 1;
            }
            else
            {
                var comparison = string.CompareOrdinal(a, b);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }

        return left.Count.CompareTo(right.Count);
    }
}
