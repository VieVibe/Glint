using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CSharp.RuntimeBinder;

namespace Glint.App.Services;

internal readonly record struct ExplorerContext(IntPtr Window, IntPtr View, bool Desktop);

internal static class ExplorerSelection
{
    private static readonly SemaphoreSlim SelectionSlot = new(1);
    // Only accept keyboard focus inside the file view. Address/search boxes, rename edits,
    // navigation trees, menus and file dialogs keep their normal Space behavior.
    public static bool TryGetContext(out ExplorerContext context)
    {
        context = default;
        var window = GetForegroundWindow();
        var windowClass = ClassName(window);
        var desktop = windowClass is "Progman" or "WorkerW";
        if (!desktop && windowClass is not ("CabinetWClass" or "ExploreWClass")) return false;
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, out _), ref info) || (info.Flags & 0x1c) != 0) return false;
        var fileControl = false;
        var remaining = 32;
        for (var current = info.Focus; current != IntPtr.Zero && remaining-- > 0; current = GetParent(current))
        {
            var name = ClassName(current);
            if (name.Contains("Edit", StringComparison.OrdinalIgnoreCase) || name == "ComboBox") return false;
            if (name is "DirectUIHWND" or "SysListView32") fileControl = true;
            if (name == "SHELLDLL_DefView" && fileControl)
            { context = new(window, current, desktop); return true; }
            if (current == window) break;
        }
        return false;
    }

    public static Task<string?> GetSelectedFileAsync(ExplorerContext context, Action<string>? diagnostic = null)
    {
        // An unresponsive Explorer must never accumulate background COM threads.
        if (!SelectionSlot.Wait(0)) return Task.FromResult<string?>(null);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(GetSelectedFile(context, diagnostic)); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { SelectionSlot.Release(); }
        }) { IsBackground = true, Name = "Glint Explorer selection" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch { SelectionSlot.Release(); throw; }
        return completion.Task;
    }

    private static string? GetSelectedFile(ExplorerContext context, Action<string>? diagnostic)
    {
        object? windows = null;
        try
        {
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            if (context.Desktop)
            {
                object location = 0, root = 0;
                object? desktop = null;
                try
                {
                    desktop = ((dynamic)windows!).FindWindowSW(ref location, ref root, 8, out int _, 1);
                    return desktop is null ? null : ReadSelection(desktop, context.View, diagnostic);
                }
                finally { Release(desktop); }
            }
            var count = (int)((dynamic)windows!).Count;
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                try
                {
                    browser = ((dynamic)windows).Item(index);
                    if (browser is null || new IntPtr((long)((dynamic)browser).HWND) != context.Window) continue;
                    var selected = ReadSelection(browser, context.View, diagnostic);
                    if (selected is not null) return selected;
                }
                catch (Exception exception) when (exception is COMException or InvalidCastException or RuntimeBinderException)
                { diagnostic?.Invoke(exception.Message); }
                finally { Release(browser); }
            }
            return null;
        }
        finally { Release(windows); }
    }

    private static string? ReadSelection(object browser, IntPtr expectedView, Action<string>? diagnostic)
    {
        object? shellBrowser = null, view = null, folderView = null, items = null, item = null;
        try
        {
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
            var browserId = typeof(IShellBrowser).GUID;
            ((IShellServiceProvider)browser).QueryService(ref service, ref browserId, out shellBrowser);
            ((IShellBrowser)shellBrowser).QueryActiveShellView(out view);
            ((IShellView)view).GetWindow(out var viewWindow);
            diagnostic?.Invoke($"Shell view: {viewWindow}; focused view: {expectedView}");
            // Windows 11 tabs share a top-level HWND; match the actual focused folder view.
            if (viewWindow != expectedView) return null;
            var dispatch = new Guid("00020400-0000-0000-C000-000000000046");
            ((IShellView)view).GetItemObject(0, ref dispatch, out folderView);
            items = ((dynamic)folderView).SelectedItems();
            diagnostic?.Invoke($"Selected items: {((dynamic)items).Count}");
            if ((int)((dynamic)items).Count == 0) return null;
            item = ((dynamic)items).Item(0);
            if ((bool)((dynamic)item).IsFolder || !(bool)((dynamic)item).IsFileSystem) return null;
            return (string)((dynamic)item).Path;
        }
        finally { Release(item); Release(items); Release(folderView); Release(view); Release(shellBrowser); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        void QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp(int enter);
        void InsertMenusSB(IntPtr menu, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr oleMenu, IntPtr active);
        void RemoveMenusSB(IntPtr menu);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(int enable);
        void TranslateAcceleratorSB(IntPtr message, ushort id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr window);
        void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr window);
        void ContextSensitiveHelp(int enter);
        void TranslateAccelerator(IntPtr message);
        void EnableModeless(int enable);
        void UIActivate(uint state);
        void Refresh();
        void CreateViewWindow(IntPtr previous, IntPtr settings, IntPtr browser, IntPtr rectangle, out IntPtr window);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr settings);
        void AddPropertySheetPages(uint reserved, IntPtr callback, IntPtr param);
        void SaveViewState();
        void SelectItem(IntPtr pidl, uint flags);
        void GetItemObject(uint selection, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object value);
    }

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
}
