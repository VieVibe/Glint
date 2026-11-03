namespace Glint.Core;

public static class AppPreferences
{
    public static string[] ParseAliases(string text)
    {
        var aliases = text.Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (aliases.Length > 12) throw new ArgumentException("最多设置 12 个别名。");
        if (aliases.Any(alias => alias.Length > 40)) throw new ArgumentException("每个别名最多 40 个字符。");
        if (aliases.Any(alias => SearchEngine.Normalize(alias).Length == 0))
            throw new ArgumentException("别名需要包含文字或数字。");
        return aliases;
    }
}
