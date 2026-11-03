using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Glint.App.Services;

public sealed class IconService
{
    private readonly ConcurrentDictionary<string, Lazy<Task<ImageSource?>>> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim extractionSlots = new(2);
    public Task<ImageSource?> GetAsync(string path)
    {
        if (path.Length == 0) return Task.FromResult<ImageSource?>(null);
        return cache.GetOrAdd(path, key => new Lazy<Task<ImageSource?>>(() => ExtractAsync(key),
            LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // A repeated search shares its pending extraction and caches misses as well as successes.
    public void Clear() => cache.Clear();

    private async Task<ImageSource?> ExtractAsync(string path)
    {
        await extractionSlots.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => Extract(path)).ConfigureAwait(false); }
        finally { extractionSlots.Release(); }
    }

    private static ImageSource? Extract(string path)
    {
        var info = new ShellFileInfo();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x1) == IntPtr.Zero || info.Icon == IntPtr.Zero) return null;
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return (ImageSource?)bitmap;
        }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
