using System.Text.Json;

namespace Glint.Core;

public sealed class SettingsStore(string directory)
{
    public string FilePath { get; } = Path.Combine(directory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public UserSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Options)
            ?? throw new InvalidDataException("设置文件内容为空。");
        settings.PortableApps ??= [];
        settings.Usage = new(settings.Usage ?? [], StringComparer.OrdinalIgnoreCase);
        settings.PinnedApps = new(settings.PinnedApps ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Aliases = new(settings.Aliases ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var id in settings.Aliases.Keys.ToArray())
            settings.Aliases[id] = (settings.Aliases[id] ?? []).Where(alias => !string.IsNullOrWhiteSpace(alias)).ToArray();
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
