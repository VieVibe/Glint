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
    private const double MinZoom = 50, DefaultZoom = 100, MaxZoom = 300, ZoomStep = 10;
    private static readonly SemaphoreSlim LoadSlots = new(2);
    private readonly string path;
    private readonly double defaultFontSize;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private PdfDocument? pdf;
    private IRandomAccessStream? pdfInput;
    private Task? loading, rendering;
    private uint pageIndex;
    private double zoom = DefaultZoom;
    private bool closing, closed, playing, seeking, updatingPosition, opening, activated;
    internal Task Completion { get; private set; } = Task.CompletedTask;

    public PreviewWindow(string path)
    {
        this.path = path;
        InitializeComponent();
        defaultFontSize = DocumentText.FontSize;
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        Title = name + " · 快速预览 · 轻启 Glint";
        FileNameLabel.Text = name;
        FileNameLabel.ToolTip = path;
        FileTypeLabel.Text = FilePreview.IsEnvironmentFile(path) ? "ENV"
            : (extension.Length > 1 ? extension[1..] : name).ToUpperInvariant();
        FileTypeLabel.ToolTip = FileTypeLabel.Text;
        DocumentText.Options.EnableHyperlinks = false;
        DocumentText.Options.EnableEmailHyperlinks = false;
        DocumentText.TextArea.TextView.Margin = new Thickness(8, 0, 0, 0);
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
            DetailsLabel.Text = FormatSize(length);
            var kind = FilePreview.GetKind(path);
            switch (kind)
            {
                case FilePreviewKind.Image:
                    var bitmap = await Task.Run(() => PreviewImageLoader.Load(path), lifetime.Token);
                    if (closed) return;
                    ShowImage(bitmap);
                    StatusLabel.Text = "图片预览";
                    break;
                case FilePreviewKind.Video:
                    Video.Visibility = VisualSurface.Visibility = Visibility.Visible;
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
                    ShowDocument(document, kind);
                    break;
                default:
                    ShowMessage("此格式暂不支持预览", "按 Enter 或点击“打开”，可使用默认应用查看。");
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

    private void ShowDocument(DocumentPreview document, FilePreviewKind kind)
    {
        if (document.Text.Length == 0)
        {
            ShowMessage("文件没有可预览的文字", "");
            return;
        }

        var isText = kind == FilePreviewKind.Text;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path).ToLowerInvariant();
        var prose = (extension == ".txt" && name != "cmakelists.txt") || extension is ".md" or ".markdown" or ".rst" or ".adoc"
            || name is "readme" or "license" or "licence" or "copying" or "changelog" or "notice";
        DocumentText.ShowLineNumbers = isText && !prose;
        if (isText) DocumentText.SyntaxHighlighting = PreviewSyntax.GetDefinition(path, FilePreview.IsEnvironmentFile(path));
        WrapButton.IsChecked = !isText || prose;
        DocumentText.Text = document.Text;
        DocumentSurface.Visibility = WrapButton.Visibility = Visibility.Visible;
        ShowZoomControls();
        MessagePanel.Visibility = Visibility.Collapsed;
        if (IsActive && (Keyboard.FocusedElement is null || Keyboard.FocusedElement == this)) DocumentText.TextArea.Focus();
        var shownLines = DocumentText.Document.LineCount;
        StatusLabel.Text = (DocumentText.ShowLineNumbers ? $"{shownLines:N0} 行" : document.Format)
            + (document.Truncated ? " · 仅显示前 20 万字符" : "");
        StatusLabel.ToolTip = StatusLabel.Text;
    }

    private void ShowImage(BitmapSource bitmap)
    {
        PreviewImage.Source = bitmap;
        VisualSurface.Visibility = Visibility.Visible;
        UpdateVisualSize();
        VisualScroll.ScrollToHome();
        ShowZoomControls();
        MessagePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowZoomControls()
    {
        ZoomControls.Visibility = Visibility.Visible;
        UpdateZoomControls();
    }

    private void UpdateZoomControls()
    {
        ResetZoom.Content = $"{zoom:0}%";
        ZoomOut.IsEnabled = zoom > MinZoom;
        ZoomIn.IsEnabled = zoom < MaxZoom;
    }

    private void OnVisualSizeChanged(object sender, SizeChangedEventArgs e) => UpdateVisualSize();

    private void UpdateVisualSize()
    {
        var bitmap = PreviewImage.Source as BitmapSource;
        var width = bitmap?.Width ?? Video.NaturalVideoWidth;
        var height = bitmap?.Height ?? Video.NaturalVideoHeight;
        if (width <= 0 || height <= 0 || VisualSurface.ActualWidth <= 0 || VisualSurface.ActualHeight <= 0) return;

        var fit = Math.Min(VisualSurface.ActualWidth / width, VisualSurface.ActualHeight / height);
        VisualContent.Width = width * fit * zoom / DefaultZoom;
        VisualContent.Height = height * fit * zoom / DefaultZoom;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ZoomControls.Visibility != Visibility.Visible || e.Delta == 0) return;
        var notches = e.Delta / 120d;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            if (DocumentSurface.Visibility == Visibility.Visible)
                DocumentText.ScrollToVerticalOffset(DocumentText.VerticalOffset - notches * DocumentText.FontSize * 3);
            else
                VisualScroll.ScrollToVerticalOffset(VisualScroll.VerticalOffset - notches * 48);
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
            SetZoom(zoom + notches * ZoomStep,
                VisualSurface.Visibility == Visibility.Visible ? e.GetPosition(VisualScroll) : null);
        else return;

        e.Handled = true;
    }

    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(zoom - ZoomStep);
    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(zoom + ZoomStep);
    private void OnResetZoom(object sender, RoutedEventArgs e) => SetZoom(DefaultZoom);

    private void SetZoom(double requestedZoom, Point? pointer = null)
    {
        var previousZoom = zoom;
        zoom = Math.Clamp(Math.Round(requestedZoom), MinZoom, MaxZoom);
        if (zoom == previousZoom) return;
        UpdateZoomControls();

        if (DocumentSurface.Visibility == Visibility.Visible)
        {
            var textView = DocumentText.TextArea.TextView;
            var vertical = DocumentText.VerticalOffset;
            var horizontal = DocumentText.HorizontalOffset;
            var line = vertical > 0 ? textView.GetDocumentLineByVisualTop(vertical) : null;
            var withinLine = line is null ? 0 : vertical - textView.GetVisualTopByDocumentLine(line.LineNumber);
            DocumentText.FontSize = defaultFontSize * zoom / DefaultZoom;
            if (line is not null)
            {
                DocumentText.UpdateLayout();
                DocumentText.ScrollToVerticalOffset(textView.GetVisualTopByDocumentLine(line.LineNumber) + withinLine * zoom / previousZoom);
            }
            if (horizontal > 0) DocumentText.ScrollToHorizontalOffset(horizontal * zoom / previousZoom);
        }
        else
        {
            var viewport = new Rect(0, 0, VisualScroll.ViewportWidth, VisualScroll.ViewportHeight);
            var anchor = pointer is { } position && viewport.Contains(position) ? position
                : new Point(viewport.Width / 2, viewport.Height / 2);
            var oldOrigin = VisualContent.TranslatePoint(new Point(), VisualScroll);
            var relativeX = VisualContent.ActualWidth > 0 ? Math.Clamp((anchor.X - oldOrigin.X) / VisualContent.ActualWidth, 0, 1) : .5;
            var relativeY = VisualContent.ActualHeight > 0 ? Math.Clamp((anchor.Y - oldOrigin.Y) / VisualContent.ActualHeight, 0, 1) : .5;
            UpdateVisualSize();
            VisualScroll.UpdateLayout();
            var newAnchor = VisualContent.TranslatePoint(new Point(relativeX * VisualContent.ActualWidth, relativeY * VisualContent.ActualHeight), VisualScroll);
            VisualScroll.ScrollToHorizontalOffset(VisualScroll.HorizontalOffset + newAnchor.X - anchor.X);
            VisualScroll.ScrollToVerticalOffset(VisualScroll.VerticalOffset + newAnchor.Y - anchor.Y);
        }
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
            ShowImage(bitmap);
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
        UpdateVisualSize();
        if (Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0) ShowZoomControls();
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
        VisualSurface.Visibility = DocumentSurface.Visibility = WrapButton.Visibility = Video.Visibility
            = PdfControls.Visibility = VideoControls.Visibility = ZoomControls.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
        MessageTitle.Text = title;
        MessageHint.Text = hint;
        MessageHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StatusLabel.Text = "快速预览";
        StatusLabel.ToolTip = null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && WrapButton.Visibility == Visibility.Visible)
        {
            WrapButton.IsChecked = WrapButton.IsChecked != true;
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Space or Key.Escape) { e.Handled = true; RequestClose(); }
        else if (e.Key == Key.Enter)
        {
            if (ZoomControls.IsKeyboardFocusWithin) return;
            e.Handled = true;
            if (WrapButton.IsKeyboardFocused) WrapButton.IsChecked = WrapButton.IsChecked != true;
            else if (CloseButton.IsKeyboardFocused) RequestClose();
            else OpenFile();
        }
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
