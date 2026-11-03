using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Glint.App.Services;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private int registeredId;
    private string? registeredHotkey;
    public event Action? Pressed;

    public HotkeyService(IntPtr handle)
    {
        source = HwndSource.FromHwnd(handle)!;
        source.AddHook(WindowHook);
    }

    public bool TryRegister(string hotkey)
    {
        if (registeredId != 0 && registeredHotkey == hotkey) return true;
        var nextId = registeredId == 1 ? 2 : 1;
        uint modifiers = 0x4001; // ALT + NOREPEAT
        if (hotkey == "Ctrl+Alt+Space") modifiers |= 0x0002;
        if (!RegisterHotKey(source.Handle, nextId, modifiers, 0x20)) return false;
        if (registeredId != 0) UnregisterHotKey(source.Handle, registeredId);
        registeredId = nextId;
        registeredHotkey = hotkey;
        return true;
    }

    private IntPtr WindowHook(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x0312 && wParam.ToInt32() == registeredId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (registeredId != 0) UnregisterHotKey(source.Handle, registeredId);
        source.RemoveHook(WindowHook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
