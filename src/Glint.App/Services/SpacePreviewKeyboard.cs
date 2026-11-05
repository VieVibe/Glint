using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Glint.App.Services;

internal sealed class SpacePreviewKeyboard : IDisposable
{
    private readonly Thread thread;
    private readonly HookCallback callback;
    private readonly Dispatcher dispatcher;
    private readonly Action<ExplorerContext?> pressed;
    private readonly ManualResetEventSlim ready = new();
    private IntPtr hook, previewWindow;
    private uint threadId;
    private Exception? startError;
    private bool spaceDown, suppressSpace, escapeDown;
    private int disposed;
    public volatile bool Enabled = true;

    public SpacePreviewKeyboard(Dispatcher dispatcher, Action<ExplorerContext?> pressed)
    {
        this.dispatcher = dispatcher;
        this.pressed = pressed;
        callback = OnKeyboard;
        thread = new Thread(Run) { IsBackground = true, Name = "Glint Space preview keyboard" };
        thread.Start();
        ready.Wait();
        if (startError is not null) { thread.Join(); ready.Dispose(); throw startError; }
    }

    public void SetPreviewWindow(IntPtr window) => Interlocked.Exchange(ref previewWindow, window);

    private void Run()
    {
        var started = false;
        try
        {
            threadId = GetCurrentThreadId();
            // Establish the message queue before allowing Dispose to post WM_QUIT.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) startError = new Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            started = true;
            if (hook == IntPtr.Zero) return;
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref message); DispatchMessage(ref message); }
        }
        catch (Exception exception)
        {
            if (!started) startError = exception;
            else { Enabled = false; AppDiagnostics.Log("Preview keyboard loop", exception); }
        }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (!started) ready.Set();
        }
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        try { return ProcessKeyboard(code, wParam, lParam); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never allow a recoverable managed exception to escape a native hook callback.
            Enabled = false;
            spaceDown = suppressSpace = escapeDown = false;
            try { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => AppDiagnostics.Log("Preview keyboard callback", exception)); }
            catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
    }

    private IntPtr ProcessKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var key = Marshal.PtrToStructure<KeyboardData>(lParam);
        var up = wParam.ToInt32() is 0x0101 or 0x0105;
        var down = wParam.ToInt32() is 0x0100 or 0x0104;
        if (key.Key == 0x20)
        {
            if (up)
            {
                var suppress = suppressSpace;
                spaceDown = suppressSpace = false;
                if (suppress) return new IntPtr(1);
            }
            else if (down)
            {
                if (spaceDown) return suppressSpace ? new IntPtr(1) : CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
                spaceDown = true;
                var preview = Interlocked.CompareExchange(ref previewWindow, IntPtr.Zero, IntPtr.Zero);
                if (Enabled && !HasModifiers())
                {
                    if (preview != IntPtr.Zero && ExplorerSelection.GetForegroundWindow() == preview)
                    { suppressSpace = true; Queue(null); }
                    else if (ExplorerSelection.TryGetContext(out var context))
                    { suppressSpace = true; Queue(context); }
                }
                if (suppressSpace) return new IntPtr(1);
            }
        }
        else if (key.Key == 0x1b)
        {
            if (up && escapeDown) { escapeDown = false; return new IntPtr(1); }
            var preview = Interlocked.CompareExchange(ref previewWindow, IntPtr.Zero, IntPtr.Zero);
            if (down && (escapeDown || (preview != IntPtr.Zero && ExplorerSelection.GetForegroundWindow() == preview && !HasModifiers())))
            {
                if (!escapeDown) Queue(null);
                escapeDown = true;
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void Queue(ExplorerContext? context)
    {
        try { if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => pressed(context)); }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted) { }
    }

    private static bool HasModifiers() => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Enabled = false;
        PostThreadMessage(threadId, 0x0012, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(TimeSpan.FromSeconds(3))) AppDiagnostics.Log("Preview keyboard shutdown", new TimeoutException("Keyboard listener did not stop in time."));
        ready.Dispose();
        GC.KeepAlive(callback);
    }

    private delegate IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, ScanCode, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message
    { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookCallback callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
}
