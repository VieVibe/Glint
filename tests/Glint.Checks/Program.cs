using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Glint.App.Services;
using Glint.Core;

internal static class Program
{
    private static int passed;

    [STAThread]
    public static async Task Main()
    {
        var apps = new[]
        {
            new AppEntry("wechat", "微信", "wechat.exe", "test"),
            new AppEntry("meeting", "腾讯会议", "meeting.exe", "test"),
            new AppEntry("code", "Visual Studio Code", "code.exe", "test"),
            new AppEntry("chrome", "Google Chrome", "chrome.exe", "test"),
            new AppEntry("codehelper", "Code Helper", "helper.exe", "test")
        };
        var engine = new SearchEngine(apps);
        var usage = new Dictionary<string, UsageInfo>();
        foreach (var query in new[] { "微信", "weixin", "wx", "WeChat", "ｗｘ" })
            Check(engine.Search(query, usage).First().Id == "wechat", "微信匹配 " + query);
        foreach (var query in new[] { "tengxunhuiyi", "txhy", "腾讯" })
            Check(engine.Search(query, usage).First().Id == "meeting", "任意中文应用拼音 " + query);
        Check(engine.Search("VS Code", usage).First().Id == "code", "英文别名与空格");
        Check(engine.Search("浏览器", usage).First().Id == "chrome", "类别别名");
        Check(engine.Search("unmatched_xyz", usage).Count == 0, "无匹配时为空");
        usage["codehelper"] = new(10000, DateTimeOffset.UtcNow);
        Check(engine.Search("vscode", usage).First().Id == "code", "使用次数不会越过匹配相关性");
        Check(engine.Search("", usage).First().Id == "codehelper", "空查询按使用次数排序");
        Check(engine.Search("", usage, 2).Count == 2, "结果数量限制");
        Check(engine.Search("sdc", usage).Any(entry => entry.Id == "code"), "英文子序列容错");

        var identityEngine = new SearchEngine([
            new("personal", "Weixin", "personal.exe", "test"),
            new("business", "企业微信", "business.exe", "test", Keywords: "WXWork"),
            new("noise", "Knowledge Editor", "editor.exe", "test"),
            new("browser", "Google Chrome", "chrome.exe", "test")
        ]);
        var identityUsage = new Dictionary<string, UsageInfo> { ["business"] = new(100000, DateTimeOffset.UtcNow) };
        Check(identityEngine.Search("wx", identityUsage).First().Id == "personal", "企业微信不会抢占 wx 别名");
        Check(identityEngine.Search("wechat", identityUsage).All(app => app.Id != "business"), "企业微信不继承普通微信的英文别名");
        Check(identityEngine.Search("qywx", identityUsage).Single().Id == "business", "企业微信仍可用 qywx 搜索");
        Check(identityEngine.Search("微软浏览器", identityUsage).All(app => app.Id != "noise"), "英文名称片段不会冒认浏览器类别别名");

        var pinned = new HashSet<string> { "wechat" };
        Check(engine.Search("", usage, pinned: pinned).First().Id == "wechat", "固定应用在空查询时优先");
        Check(engine.Search("vscode", usage, pinned: new HashSet<string> { "codehelper" }).First().Id == "code", "固定应用不能盖过准确匹配");
        var aliases = new Dictionary<string, string[]> { ["code"] = ["写代码", "常用编辑器", "浏览器"] };
        var customEngine = new SearchEngine(apps, aliases);
        Check(customEngine.Search("写代码", usage).First().Id == "code", "自定义中文别名");
        Check(customEngine.Search("xdm", usage).First().Id == "code", "自定义别名支持拼音首字母");
        Check(customEngine.Search("changyongbianjiqi", usage).First().Id == "code", "自定义别名支持全拼");
        Check(customEngine.Search("用编辑", usage).First().Id == "code", "自定义别名支持中间片段");
        Check(customEngine.Search("浏览器", usage).First().Id == "code", "用户指定的精确别名优于内置类别别名");
        Check(AppPreferences.ParseAliases("工作, 写代码；WORK\n工作，开发").SequenceEqual(["工作", "写代码", "WORK", "开发"]), "别名分隔符与去重");
        Check(AppPreferences.ParseAliases("  \n").Length == 0, "可以清空别名");
        Check(ThrowsArgument(() => AppPreferences.ParseAliases("---")), "拒绝纯标点别名");
        Check(ThrowsArgument(() => AppPreferences.ParseAliases(new string('x', 41))), "限制单个别名长度");
        Check(ThrowsArgument(() => AppPreferences.ParseAliases(string.Join(',', Enumerable.Range(0, 13)))), "限制别名数量");
        foreach (var (area, scale) in new[]
                 { (new DesktopBounds(0, 0, 1920, 1040), 1d), (new DesktopBounds(0, 0, 1366, 728), 2d),
                   (new DesktopBounds(-1280, 0, 1280, 680), 1.5d), (new DesktopBounds(0, -900, 1440, 860), 1.25d) })
        {
            var bounds = LauncherPlacement.Fit(area, scale);
            Check(bounds.X >= area.X && bounds.Y >= area.Y && bounds.X + bounds.Width <= area.X + area.Width
                && bounds.Y + bounds.Height <= area.Y + area.Height, $"窗口不越过工作区 {area.Width}x{area.Height} scale={scale}");
        }

        var directory = Path.Combine(Path.GetTempPath(), "Glint.Checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(directory);
            Check(store.Load().Hotkey == "Alt+Space", "默认设置");
            var settings = new UserSettings { Hotkey = "Ctrl+Alt+Space", PortableApps = [Path.Combine(directory, "便携工具", "demo.exe")], Usage = usage,
                PinnedApps = new(StringComparer.OrdinalIgnoreCase) { "WECHAT" }, Aliases = aliases };
            store.Save(settings);
            var loaded = store.Load();
            Check(loaded.Hotkey == settings.Hotkey && loaded.PortableApps.SequenceEqual(settings.PortableApps)
                && loaded.Usage["CODEHELPER"].Count == 10000, "设置与中文路径持久化");
            store.Save(loaded);
            Check(!File.Exists(store.FilePath + ".tmp"), "原子保存清理临时文件");
            Check(loaded.PinnedApps.Contains("wechat") && loaded.Aliases["CODE"].Contains("写代码"), "固定和别名兼容大小写并持久化");
            File.WriteAllText(store.FilePath, "{\"Hotkey\":\"Alt+Space\",\"Usage\":{}}");
            var legacy = store.Load();
            Check(legacy.PinnedApps.Count == 0 && legacy.Aliases.Count == 0, "兼容旧版设置无需重置");
            File.WriteAllText(store.FilePath, "{broken");
            var rejectedCorruption = false;
            try { store.Load(); } catch (JsonException) { rejectedCorruption = true; }
            Check(rejectedCorruption, "损坏设置可检测");

            var executable = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            var shortcutPath = Path.Combine(directory, "Glint 索引验证.lnk");
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
            try { ((dynamic)shortcut).TargetPath = executable; ((dynamic)shortcut).Save(); }
            finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
            var catalog = await new ApplicationCatalog().ScanAsync([shortcutPath, executable]);
            var notepadId = "file:" + executable.ToLowerInvariant() + "|";
            Check(catalog.Apps.Count(entry => entry.Id == notepadId) == 1, "真实快捷方式解析并按目标去重");
            Console.WriteLine($"实际索引：{catalog.Apps.Length} 个应用；警告：{string.Join("；", catalog.Warnings)}");
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var monitor = new CatalogMonitor([directory]))
            {
                monitor.Changed += () => changed.TrySetResult();
                File.WriteAllText(Path.Combine(directory, "新应用.lnk"), "fixture");
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Check(changed.Task.IsCompletedSuccessfully, "开始菜单变化自动触发刷新");
            }
        }
        finally
        {
            // This directory is freshly created by this test and contains only its own fixtures.
            Directory.Delete(directory, recursive: true);
        }

        var large = Enumerable.Range(0, 2000).Select(index => new AppEntry("app" + index, "测试应用 " + index, "test.exe", "test"));
        var largeEngine = new SearchEngine(large);
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 200; index++) largeEngine.Search("csyy", usage);
        watch.Stop();
        Console.WriteLine($"搜索基准：2000 个应用，200 次查询，平均 {watch.Elapsed.TotalMilliseconds / 200:F2} ms/次（不含 UI）");
        Console.WriteLine($"PASS: {passed} checks");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static bool ThrowsArgument(Action action)
    {
        try { action(); return false; }
        catch (ArgumentException) { return true; }
    }
}
