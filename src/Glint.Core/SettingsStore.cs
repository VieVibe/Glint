using System.Text.Json;

namespace Glint.Core;

public sealed class SettingsStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 4_000_000) throw new InvalidDataException("设置文件过大。");
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException("设置文件内容为空。");
        settings.PortableApps = (settings.PortableApps ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var usage = new Dictionary<string, UsageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Usage ?? [])
            if (pair.Value is not null) usage[pair.Key] = pair.Value with { Count = Math.Clamp(pair.Value.Count, 0, 1_000_000) };
        settings.Usage = usage;
        settings.PinnedApps = new(settings.PinnedApps ?? [], StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in settings.Aliases ?? [])
            aliases[pair.Key] = (pair.Value ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias.Length > 40 ? alias[..40] : alias).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        settings.Aliases = aliases;
        if (settings.Hotkey is not ("Alt+Space" or "Ctrl+Alt+Space")) settings.Hotkey = "Alt+Space";
        return settings;
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(directory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
