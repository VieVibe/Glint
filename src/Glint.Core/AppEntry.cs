namespace Glint.Core;

public sealed record AppEntry(string Id, string Name, string LaunchPath, string Source,
    string IconPath = "", string Keywords = "");

public sealed record UsageInfo(int Count, DateTimeOffset LastUsed);

public sealed class UserSettings
{
    public string Hotkey { get; set; } = "Alt+Space";
    public bool SpacePreviewEnabled { get; set; } = true;
    public List<string> PortableApps { get; set; } = [];
    public Dictionary<string, UsageInfo> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PinnedApps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
