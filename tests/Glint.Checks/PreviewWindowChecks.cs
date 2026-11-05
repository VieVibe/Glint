using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glint.App;
using Glint.App.Services;
using Glint.Core;

internal static class PreviewWindowChecks
{
    public static void Run(Action<bool, string> check, int stressCycles = 100)
    {
        // Use hidden native windows on an STA thread; no foreground app or user file is touched.
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var preview = new PreviewWindow(Path.Combine(Path.GetTempPath(), "Glint-unopened-preview.txt"));
                check(preview.Topmost && preview.ShowActivated, "快速预览默认置顶并请求激活");
                new WindowInteropHelper(preview).EnsureHandle();
                var closes = 0;
                var deactivatedDuringClose = false;
                preview.Closing += (_, _) =>
                {
                    deactivatedDuringClose = true;
                    // Raise the real event during Closing to reproduce the crash report's ordering.
                    typeof(Window).GetMethod("OnDeactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(preview, [EventArgs.Empty]);
                    preview.RequestClose();
                };
                preview.Closed += (_, _) => closes++;
                preview.RequestClose();
                preview.RequestClose();
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
                Dispatcher.PushFrame(frame);
                check(closes == 1 && deactivatedDuringClose, "关闭期间再次失焦、重复关闭不会崩溃且只关闭一次");
                CheckLoadingAndStress(check, stressCycles);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true, Name = "Glint preview lifecycle checks" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("预览窗口生命周期检查失败。", failure);
    }

    private static void CheckLoadingAndStress(Action<bool, string> check, int stressCycles)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Glint.Preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var image = Path.Combine(directory, "长图片.png");
            WriteImage(image, 1, 16000);
            var bitmap = PreviewImageLoader.Load(image);
            check(bitmap.PixelWidth <= PreviewLimits.ImageDimension && bitmap.PixelHeight <= PreviewLimits.ImageDimension, "超长图片按双向尺寸限制解码");
            WriteImage(image, 16000, 1);
            bitmap = PreviewImageLoader.Load(image);
            check(bitmap.PixelWidth <= PreviewLimits.ImageDimension && bitmap.PixelHeight <= PreviewLimits.ImageDimension, "超宽图片按双向尺寸限制解码");
            var pdf = Path.Combine(directory, "页面.pdf");
            WritePdf(pdf);
            var text = Path.Combine(directory, "文字.txt");
            File.WriteAllText(text, "中文预览稳定性检查");
            var window = Create(pdf);
            Pump(window.LoadPreviewAsync());
            check(((Image)window.FindName("PreviewImage")).Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }
                && ((TextBlock)window.FindName("PageLabel")).Text == "1 / 1", "PDF 使用系统渲染器实际加载页面");
            window.RequestClose();
            Pump(window.Completion);
            using (new FileStream(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            check(true, "PDF 预览关闭后立即释放文件");
            var broken = Path.Combine(directory, "损坏.png");
            File.WriteAllText(broken, "broken image");
            window = Create(broken);
            Pump(window.LoadPreviewAsync());
            check(((TextBlock)window.FindName("MessageTitle")).Text == "无法预览此文件", "损坏图片显示错误且不会让应用退出");
            window.RequestClose();
            Pump(window.Completion);
            window = Create(pdf);
            _ = window.LoadPreviewAsync();
            window.RequestClose();
            Pump(window.Completion);
            check(true, "加载过程中关闭预览可完成取消与清理");

            void Cycle(int index)
            {
                var path = new[] { image, pdf, text }[index % 3];
                var preview = Create(path);
                Pump(preview.LoadPreviewAsync());
                preview.RequestClose();
                preview.RequestClose();
                Pump(preview.Completion);
                using var unlocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            for (var index = 0; index < 6; index++) Cycle(index);
            Collect();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var handles = process.HandleCount;
            var memory = GC.GetTotalMemory(true);
            var watch = Stopwatch.StartNew();
            for (var index = 0; index < stressCycles; index++) Cycle(index);
            watch.Stop();
            Collect();
            process.Refresh();
            var handleGrowth = process.HandleCount - handles;
            var memoryGrowth = GC.GetTotalMemory(true) - memory;
            check(handleGrowth < 80 && memoryGrowth < 20_000_000, $"连续 {stressCycles} 次图片、PDF、文字预览无崩溃、文件占用或持续资源增长");
            Console.WriteLine($"预览压力检查：{stressCycles} 次 / {watch.Elapsed.TotalSeconds:F2} s；句柄变化 {handleGrowth}；托管内存变化 {memoryGrowth / 1024d:F0} KB");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static PreviewWindow Create(string path)
    {
        var preview = new PreviewWindow(path) { ShowInTaskbar = false, ShowActivated = false };
        new WindowInteropHelper(preview).EnsureHandle();
        return preview;
    }

    private static void Pump(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            using var timeout = new Timer(_ => dispatcher.BeginInvoke(() => frame.Continue = false), null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        if (!task.IsCompleted) throw new TimeoutException("预览检查超时。");
        task.GetAwaiter().GetResult();
    }

    private static void Collect()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var idle = new TaskCompletionSource();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => idle.SetResult());
        Pump(idle.Task);
    }

    private static void WriteImage(string path, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void WritePdf(string path)
    {
        var content = "BT /F1 24 Tf 40 730 Td (Glint Preview Check) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var output = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        { offsets.Add(output.Length); output.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = output.Length;
        output.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) output.Append($"{offset:0000000000} 00000 n \n");
        output.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, output.ToString(), Encoding.ASCII);
    }
}
