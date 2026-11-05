using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glint.Core;
using Glint.App.Services;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace Glint.App;

public partial class PreviewWindow : Window
{
    private static readonly SemaphoreSlim LoadSlots = new(2);
    private readonly string path;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private PdfDocument? pdf;
    private IRandomAccessStream? pdfInput;
    private Task? loading, rendering;
    private uint pageIndex;
    private bool closing, closed, playing, seeking, updatingPosition, opening, activated;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public PreviewWindow(string path)
    {
        this.path = path;
        InitializeComponent();
        Title = Path.GetFileName(path) + " · 快速预览 · 轻启 Glint";
        FileNameLabel.Text = Path.GetFileName(path);
        FileNameLabel.ToolTip = path;
        playbackTimer.Tick += (_, _) => UpdatePlayback();
        Activated += (_, _) => activated = true;
    }

    public void ShowOnScreen(IntPtr ownerWindow)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var area = Forms.Screen.FromHandle(ownerWindow).WorkingArea;
        SetWindowPos(handle, IntPtr.Zero, area.Left + 12, area.Top + 12, 0, 0, 0x0015);
        var scale = GetDpiForWindow(handle) / 96.0;
        var width = Math.Max(1, Math.Min((int)Math.Round(960 * scale), area.Width - (int)(24 * scale)));
        var height = Math.Max(1, Math.Min((int)Math.Round(720 * scale), area.Height - (int)(24 * scale)));
        SetWindowPos(handle, IntPtr.Zero, area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height, 0x0014);
        Show();
        // Explicitly raise the preview above existing windows, including topmost launchers.
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0043);
        Activate();
        SetForegroundWindow(handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = LoadPreviewAsync();
    internal Task LoadPreviewAsync() => loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        var slot = false;
        try
        {
            await LoadSlots.WaitAsync(lifetime.Token);
            slot = true;
            var length = await Task.Run(() => new FileInfo(path).Length, lifetime.Token);
            if (closed) return;
            DetailsLabel.Text = $"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} · {FormatSize(length)}";
            switch (FilePreview.GetKind(path))
            {
                case FilePreviewKind.Image:
                    var bitmap = await Task.Run(() => PreviewImageLoader.Load(path), lifetime.Token);
                    if (closed) return;
                    PreviewImage.Source = bitmap;
                    ImageScroll.Visibility = Visibility.Visible;
                    MessagePanel.Visibility = Visibility.Collapsed;
                    StatusLabel.Text = "图片预览";
                    break;
                case FilePreviewKind.Video:
                    Video.Visibility = Visibility.Visible;
                    Video.Source = new Uri(path);
                    Video.Play();
                    playing = true;
                    StatusLabel.Text = "视频预览";
                    break;
                case FilePreviewKind.Pdf:
                    pdfInput = await Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete).AsRandomAccessStream(), lifetime.Token);
                    if (closed) return;
                    pdf = await PdfDocument.LoadFromStreamAsync(pdfInput).AsTask(lifetime.Token);
                    if (closed) return;
                    if (pdf.PageCount == 0) throw new InvalidDataException("PDF 没有可显示的页面。");
                    PdfControls.Visibility = Visibility.Visible;
                    await RenderPageAsync();
                    break;
                case FilePreviewKind.Text:
                case FilePreviewKind.Office:
                    var document = await Task.Run(() => FilePreview.ReadDocumentAsync(path, lifetime.Token), lifetime.Token);
                    if (closed) return;
                    DocumentText.Text = document.Text.Length == 0 ? "（文档没有可预览的文字）" : document.Text;
                    DocumentText.Visibility = Visibility.Visible;
                    MessagePanel.Visibility = Visibility.Collapsed;
                    StatusLabel.Text = document.Format + (document.Truncated ? " · 内容较长，仅显示前 20 万字符" : "");
                    StatusLabel.ToolTip = StatusLabel.Text;
                    break;
                default:
                    ShowMessage("此格式暂不支持预览", "可按 Enter，或点击“默认应用打开”查看此文件。");
                    break;
            }
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception exception)
        {
            AppDiagnostics.Log("Preview load", exception);
            if (!closed) ShowMessage("无法预览此文件", exception.Message);
        }
        finally { if (slot) LoadSlots.Release(); }
    }

    private async Task RenderPageAsync()
    {
        if (pdf is null || closed) return;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        try
        {
            using var page = pdf.GetPage(pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            var size = PreviewLimits.Fit(page.Size.Width, page.Size.Height, 1600, 2200);
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)size.Width, DestinationHeight = (uint)size.Height }).AsTask(lifetime.Token);
            if (closed) return;
            stream.Seek(0);
            using var input = stream.AsStreamForRead();
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input;
            bitmap.EndInit();
            bitmap.Freeze();
            PreviewImage.Source = bitmap;
            ImageScroll.Visibility = Visibility.Visible;
            MessagePanel.Visibility = Visibility.Collapsed;
            PageLabel.Text = $"{pageIndex + 1} / {pdf.PageCount}";
            StatusLabel.Text = "PDF 预览 · ← → 翻页";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception exception)
        { AppDiagnostics.Log("PDF render", exception); if (!closed) ShowMessage("无法显示 PDF 页面", exception.Message); }
        finally
        {
            if (!closed && pdf is not null)
            { PreviousPage.IsEnabled = pageIndex > 0; NextPage.IsEnabled = pageIndex + 1 < pdf.PageCount; }
        }
    }

    private void ChangePage(int delta)
    {
        if (pdf is null || rendering is { IsCompleted: false } || loading is { IsCompleted: false }) return;
        var next = (long)pageIndex + delta;
        if (next < 0 || next >= pdf.PageCount) return;
        pageIndex = (uint)next;
        rendering = RenderPageAsync();
    }

    private void OnPreviousPage(object sender, RoutedEventArgs e) => ChangePage(-1);
    private void OnNextPage(object sender, RoutedEventArgs e) => ChangePage(1);
    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (closed) return;
        MessagePanel.Visibility = Visibility.Collapsed;
        VideoControls.Visibility = Visibility.Visible;
        VideoPosition.Maximum = Video.NaturalDuration.HasTimeSpan ? Math.Max(1, Video.NaturalDuration.TimeSpan.TotalSeconds) : 1;
        VideoPosition.IsEnabled = Video.NaturalDuration.HasTimeSpan;
        playbackTimer.Start();
        UpdatePlayback();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e) { playing = false; PlayPause.Content = "重播"; }
    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (closed) return;
        playbackTimer.Stop();
        AppDiagnostics.Log("Video preview", e.ErrorException);
        ReleaseVideo();
        ShowMessage("无法播放此视频", "文件可能已损坏，或系统缺少对应的视频解码器。可使用默认应用打开。");
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (playing) Video.Pause();
        else
        {
            if (Video.NaturalDuration.HasTimeSpan && Video.Position >= Video.NaturalDuration.TimeSpan) Video.Position = TimeSpan.Zero;
            Video.Play();
        }
        playing = !playing;
        PlayPause.Content = playing ? "暂停" : "播放";
    }

    private void UpdatePlayback()
    {
        if (closed || !Video.NaturalDuration.HasTimeSpan) return;
        updatingPosition = true;
        if (!seeking) VideoPosition.Value = Video.Position.TotalSeconds;
        updatingPosition = false;
        TimeLabel.Text = $"{Video.Position:h\\:mm\\:ss} / {Video.NaturalDuration.TimeSpan:h\\:mm\\:ss}";
    }

    private void OnSeekStart(object sender, MouseButtonEventArgs e) => seeking = true;
    private void OnSeekEnd(object sender, MouseButtonEventArgs e) { seeking = false; Video.Position = TimeSpan.FromSeconds(VideoPosition.Value); }
    private void OnPositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!updatingPosition && !seeking && Video is not null && Video.NaturalDuration.HasTimeSpan)
            Video.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void ShowMessage(string title, string hint)
    {
        ImageScroll.Visibility = DocumentText.Visibility = Video.Visibility = PdfControls.Visibility = VideoControls.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageHint.Text = hint;
        StatusLabel.Text = "快速预览";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Space or Key.Escape) { e.Handled = true; RequestClose(); }
        else if (e.Key == Key.Enter) { e.Handled = true; OpenFile(); }
        else if (pdf is not null && e.Key is Key.Left or Key.Right)
        { e.Handled = true; ChangePage(e.Key == Key.Left ? -1 : 1); }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenFile();
    private void OnCloseClick(object sender, RoutedEventArgs e) => RequestClose();
    private void OpenFile()
    {
        try
        {
            opening = true;
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            RequestClose();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        { opening = false; ShowMessage("无法打开此文件", exception.Message); }
    }

    internal void RequestClose()
    {
        if (!closing && !closed) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // WM_ACTIVATE can reenter through Deactivated before Closed has fired.
        closing = true;
        base.OnClosing(e);
        if (e.Cancel) closing = false;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (opening || closing || closed || !activated) return;
        // Finish the native activation callback before closing, and tolerate transient focus changes.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!IsActive && !opening) RequestClose();
        });
    }
    private void OnClosed(object? sender, EventArgs e)
    {
        closing = true;
        closed = true;
        lifetime.Cancel();
        playbackTimer.Stop();
        ReleaseVideo();
        PreviewImage.Source = null;
        DocumentText.Clear();
        Completion = ReleaseResourcesAsync();
    }

    private void ReleaseVideo()
    {
        if (Video.Source is null) return;
        try { Video.Close(); Video.Source = null; }
        catch (Exception exception) when (exception is InvalidOperationException or ExternalException)
        { AppDiagnostics.Log("Video cleanup", exception); }
    }

    private async Task ReleaseResourcesAsync()
    {
        try
        {
            // Let cancelled native operations finish before releasing their input stream.
            if (loading is not null) await loading;
            if (rendering is not null) await rendering;
        }
        catch (Exception exception) { AppDiagnostics.Log("Preview cleanup", exception); }
        finally
        {
            pdf = null;
            try { pdfInput?.Dispose(); }
            catch (Exception exception) when (exception is InvalidOperationException or ExternalException or IOException)
            { AppDiagnostics.Log("PDF cleanup", exception); }
            pdfInput = null;
            lifetime.Dispose();
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KB" : $"{bytes} B";
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
}
