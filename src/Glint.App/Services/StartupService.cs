using Microsoft.Win32;

namespace Glint.App.Services;

public static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("Glint") is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (!enabled) { key.DeleteValue("Glint", throwOnMissingValue: false); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取程序路径。");
        var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var runtimeRoot = Directory.GetParent(runtimeDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.Parent?.FullName;
        var dotnet = runtimeRoot is null ? "" : Path.Combine(runtimeRoot, "dotnet.exe");
        var command = File.Exists(dotnet)
            ? $"\"{dotnet}\" \"{typeof(StartupService).Assembly.Location}\" --background"
            : $"\"{executable}\" --background";
        key.SetValue("Glint", command);
    }
}
