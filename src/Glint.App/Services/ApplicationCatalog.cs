using System.Runtime.InteropServices;
using Glint.Core;

namespace Glint.App.Services;

public sealed record CatalogResult(AppEntry[] Apps, string[] Warnings);

public sealed class ApplicationCatalog
{
    public Task<CatalogResult> ScanAsync(IEnumerable<string> portableApps)
    {
        var paths = portableApps.ToArray();
        var completion = new TaskCompletionSource<CatalogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(Scan(paths)); }
            catch (Exception exception) { completion.SetException(exception); }
        }) { IsBackground = true, Name = "Glint app index" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static CatalogResult Scan(string[] portableApps)
    {
        var entries = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        object? scriptShell = null;
        try
        {
            scriptShell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                         Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }.Distinct())
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(root, "*", options))
                        AddFile(file, "开始菜单", scriptShell!, entries);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { warnings.Add("部分开始菜单目录无法读取"); }
            }
            foreach (var path in portableApps) AddFile(path, "手动添加", scriptShell!, entries);
        }
        catch (COMException) { warnings.Add("开始菜单索引暂不可用"); }
        finally { Release(scriptShell); }

        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is not null)
            {
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (var index = 0; index < count; index++)
                {
                    object? item = null;
                    try
                    {
                        item = ((dynamic)items).Item(index);
                        string path = ((dynamic)item!).Path;
                        string name = ((dynamic)item).Name;
                        // Packaged applications have an AUMID; Win32 apps are handled by shortcuts.
                        if (!path.Contains('!') || path.Contains('\\') || string.IsNullOrWhiteSpace(name)) continue;
                        var id = "aumid:" + path.ToLowerInvariant();
                        entries.TryAdd(id, new(id, name, "shell:AppsFolder\\" + path, "Windows 应用"));
                    }
                    catch (COMException) { }
                    finally { Release(item); }
                }
            }
        }
        catch (COMException) { warnings.Add("Windows 应用列表暂不可用"); }
        finally { Release(items); Release(folder); Release(shell); }
        return new(entries.Values.ToArray(), warnings.Distinct().ToArray());
    }

    private static void AddFile(string path, string source, object shell, Dictionary<string, AppEntry> entries)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".lnk" or ".exe" or ".appref-ms") || !File.Exists(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (new[] { "uninstall", "卸载", "unins000", "update helper" }.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase))) return;
        object? shortcut = null;
        try
        {
            var target = path;
            var arguments = "";
            if (extension == ".lnk")
            {
                shortcut = ((dynamic)shell).CreateShortcut(path);
                target = Environment.ExpandEnvironmentVariables((string)((dynamic)shortcut).TargetPath);
                arguments = (string)((dynamic)shortcut).Arguments;
                if (!File.Exists(target) || Path.GetExtension(target).ToLowerInvariant() is not (".exe" or ".com")) return;
                if (Path.GetFileNameWithoutExtension(target).StartsWith("unins", StringComparison.OrdinalIgnoreCase)) return;
            }
            var id = "file:" + Path.GetFullPath(target).ToLowerInvariant() + "|" + arguments;
            var keywords = Path.GetFileNameWithoutExtension(target);
            var entry = new AppEntry(id, name, path, source, path, keywords);
            if (entries.TryGetValue(id, out var existing))
            {
                var preferred = source == "手动添加" ? entry : existing;
                entries[id] = preferred with { Keywords = existing.Keywords + "|" + name + "|" + existing.Name };
            }
            else entries.Add(id, entry);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        { /* Broken or inaccessible shortcuts must not abort the complete index. */ }
        finally { Release(shortcut); }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
    }
}
