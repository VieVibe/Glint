using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.CSharp.RuntimeBinder;

namespace Glint.App.Services;

internal sealed class QuickPreviewService : IDisposable
{
    private readonly SpacePreviewKeyboard keyboard;
    private PreviewWindow? window;
    private bool disposed, pending;
    private int request;

    public QuickPreviewService(Dispatcher dispatcher) => keyboard = new(dispatcher, Toggle);

    public bool Enabled
    {
        get => keyboard.Enabled;
        set { keyboard.Enabled = value; if (!value) ClosePreview(); }
    }

    private async void Toggle(ExplorerContext? context)
    {
        if (disposed || !Enabled) return;
        if (window is not null || pending) { ClosePreview(); return; }
        if (context is not { } source) return;
        var current = ++request;
        pending = true;
        try
        {
            var file = await ExplorerSelection.GetSelectedFileAsync(source).WaitAsync(TimeSpan.FromSeconds(3));
            if (disposed || current != request || !Enabled || file is null
                || !ExplorerSelection.TryGetContext(out var active) || active != source) return;
            var preview = new PreviewWindow(file);
            var previewHandle = new WindowInteropHelper(preview).EnsureHandle();
            window = preview;
            preview.Closed += (_, _) =>
            {
                if (window != preview) return;
                window = null;
                keyboard.SetPreviewWindow(IntPtr.Zero);
                // Closing with the keyboard returns focus to the original file view.
                if (ExplorerSelection.GetForegroundWindow() == previewHandle)
                    SetForegroundWindow(source.Window);
            };
            keyboard.SetPreviewWindow(previewHandle);
            preview.ShowOnScreen(source.Window);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or RuntimeBinderException or IOException
            or UnauthorizedAccessException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppDiagnostics.Log("Explorer preview", exception);
            if (current == request) window?.RequestClose();
        }
        finally { if (current == request) pending = false; }
    }

    public void ClosePreview()
    {
        request++;
        pending = false;
        window?.RequestClose();
    }

    public void Dispose()
    {
        disposed = true;
        ClosePreview();
        keyboard.Dispose();
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
