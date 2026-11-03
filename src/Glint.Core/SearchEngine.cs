using System.Text;
using TinyPinyin;

namespace Glint.Core;

public sealed class SearchEngine
{
    private readonly Candidate[] candidates;
    private sealed record Candidate(AppEntry App, string Name, string[] Keys, string[] CustomKeys);
    private sealed record AliasRule(string[] Identities, string[] Aliases, string[]? Exclusions = null);

    private static readonly AliasRule[] AliasGroups =
    [
        new(["微信", "wechat", "weixin"], ["微信", "wechat", "weixin", "wx"], ["企业微信", "wecom", "wxwork"]),
        new(["企业微信", "wecom", "wxwork"], ["企业微信", "wecom", "qiyeweixin", "qywx"]),
        new(["钉钉", "dingtalk"], ["钉钉", "dingtalk", "dingding", "dd"]),
        new(["飞书", "feishu", "lark"], ["飞书", "feishu", "lark", "fs"]),
        new(["chrome", "google chrome", "谷歌浏览器"], ["chrome", "谷歌浏览器", "guge", "浏览器"]),
        new(["edge", "microsoft edge", "msedge", "微软浏览器"], ["edge", "微软浏览器", "浏览器"]),
        new(["firefox", "mozilla firefox", "火狐"], ["firefox", "火狐", "浏览器"]),
        new(["visual studio code", "vscode", "code"], ["visual studio code", "vscode", "vs code", "代码编辑器"]),
        new(["notepad", "记事本"], ["notepad", "记事本"]),
        new(["calculator", "计算器", "calc"], ["calculator", "计算器"]),
        new(["explorer", "文件资源管理器"], ["explorer", "文件资源管理器", "文件管理器"]),
        new(["qq音乐", "qqmusic"], ["qq音乐", "qqmusic", "qq music"]),
        new(["网易云音乐", "cloudmusic"], ["网易云音乐", "cloudmusic", "netease music", "wyy"])
    ];

    public SearchEngine(IEnumerable<AppEntry> apps, IReadOnlyDictionary<string, string[]>? customAliases = null)
    {
        candidates = apps.Select(app =>
        {
            var names = new List<string> { app.Name };
            names.AddRange(app.Keywords.Split('|', StringSplitOptions.RemoveEmptyEntries));
            var identityNames = names.Select(Normalize).ToArray();
            foreach (var group in AliasGroups)
            {
                if (group.Exclusions?.Any(exclusion => identityNames.Any(name => name.Contains(Normalize(exclusion), StringComparison.Ordinal))) == true) continue;
                if (group.Identities.Any(identity => identityNames.Any(name => MatchesIdentity(name, Normalize(identity)))))
                    names.AddRange(group.Aliases);
            }
            var keys = names.SelectMany(GetKeys).Where(key => key.Length > 0).Distinct().ToArray();
            var customKeys = customAliases is not null && customAliases.TryGetValue(app.Id, out var aliases)
                ? aliases.SelectMany(GetKeys).Where(key => key.Length > 0).Distinct().ToArray() : [];
            return new Candidate(app, Normalize(app.Name), keys.Concat(customKeys).Distinct().ToArray(), customKeys);
        }).ToArray();
    }

    public IReadOnlyList<AppEntry> Search(string query, IReadOnlyDictionary<string, UsageInfo> usage, int limit = 8,
        IReadOnlySet<string>? pinned = null)
    {
        var normalized = Normalize(query);
        return candidates.Select(candidate =>
        {
            usage.TryGetValue(candidate.App.Id, out var history);
            var relevance = normalized.Length == 0 ? 0 : Score(candidate, normalized);
            return new { candidate.App, Score = relevance, History = history };
        }).Where(result => result.Score >= 0)
          .OrderByDescending(result => result.Score)
          .ThenByDescending(result => pinned?.Contains(result.App.Id) ?? false)
          .ThenByDescending(result => result.History?.Count ?? 0)
          .ThenByDescending(result => result.History?.LastUsed ?? DateTimeOffset.MinValue)
          .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
          .Take(limit).Select(result => result.App).ToArray();
    }

    private static int Score(Candidate candidate, string query)
    {
        if (candidate.Name == query) return 1000;
        var best = candidate.CustomKeys.Any(key => key == query) ? 950
            : candidate.CustomKeys.Any(key => key.StartsWith(query, StringComparison.Ordinal)) ? 750 : -1;
        foreach (var key in candidate.Keys)
        {
            var score = key == query ? 900
                : key.StartsWith(query, StringComparison.Ordinal) ? 700
                : key.Contains(query, StringComparison.Ordinal) ? 500
                : query.Length >= 3 && IsSubsequence(query, key) ? 200 : -1;
            best = Math.Max(best, score);
        }
        return best;
    }

    private static bool MatchesIdentity(string name, string identity) => name == identity
        || name.StartsWith(identity, StringComparison.Ordinal) && name.Length > identity.Length
            && char.IsDigit(name[identity.Length]);

    private static bool IsSubsequence(string query, string value)
    {
        var position = 0;
        foreach (var character in value)
            if (query[position] == character && ++position == query.Length) return true;
        return false;
    }

    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static IEnumerable<string> GetKeys(string value)
    {
        yield return Normalize(value);
        var full = new StringBuilder();
        var initials = new StringBuilder();
        var wordStart = true;
        foreach (var character in value)
        {
            if (PinyinHelper.IsChinese(character))
            {
                var syllable = PinyinHelper.GetPinyin(character);
                full.Append(syllable);
                if (syllable.Length > 0) initials.Append(syllable[0]);
                wordStart = true;
            }
            else
            {
                full.Append(character);
                if (char.IsLetterOrDigit(character) && wordStart) initials.Append(character);
                wordStart = !char.IsLetterOrDigit(character);
            }
        }
        yield return Normalize(full.ToString());
        yield return Normalize(initials.ToString());
    }
}
