using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Glint.App;
using Glint.App.Services;
using Glint.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;

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
            var code = Path.Combine(directory, "project.csproj");
            File.WriteAllText(code, "<Project>\n    <PropertyGroup />\n</Project>");
            var codeWindow = Create(code);
            Pump(codeWindow.LoadPreviewAsync());
            var editor = (TextEditor)codeWindow.FindName("DocumentText");
            check(editor is { ShowLineNumbers: true, SyntaxHighlighting: not null, WordWrap: false, IsReadOnly: true }
                && editor.Document.LineCount == 3 && !editor.Options.EnableHyperlinks,
                "项目文件在只读源码视图中显示行号、高亮和原始行宽");
            ((System.Windows.Controls.Primitives.ToggleButton)codeWindow.FindName("WrapButton")).IsChecked = true;
            check(editor.WordWrap, "换行开关更新源码视图");
            var fontSize = editor.FontSize;
            Wheel(codeWindow, editor, -120);
            check(editor.FontSize == fontSize, "直接滚轮浏览源码时保持字号");
            Wheel(codeWindow, editor, 120, shift: true);
            check(editor.FontSize > fontSize && editor.IsReadOnly, "Shift 加滚轮放大源码字号");
            Wheel(codeWindow, editor, -120, shift: true);
            check(editor.FontSize == fontSize, "反向滚轮恢复源码字号");
            Wheel(codeWindow, editor, 3600, shift: true);
            check(editor.FontSize == 3 * fontSize && !((Button)codeWindow.FindName("ZoomIn")).IsEnabled, "滚轮放大限制在 300%");
            Wheel(codeWindow, editor, -3600, shift: true);
            check(editor.FontSize == fontSize / 2 && !((Button)codeWindow.FindName("ZoomOut")).IsEnabled, "滚轮缩小限制在 50%");
            codeWindow.RequestClose();
            Pump(codeWindow.Completion);

            CheckWorkbook(directory, check);

            var markdown = Path.Combine(directory, "README.markdown");
            const string markdownSource = """
                # 预览标题

                <div align="center">
                ## Mars Admin

                **居中正文**与<img src="logo.svg" alt="内嵌图片">。
                </div>

                正文 **加粗** 和 `inline()`，[项目主页](https://example.test)。

                - [x] 已完成

                > 引用内容

                | 项目 | 状态 |
                | --- | ---: |
                | Glint | 正常 |

                ```csharp
                Console.WriteLine("Glint");
                ```

                ```html
                <div class="example">代码示例</div>
                ```

                ![图片说明](https://example.test/image.png)

                <p align="right">HTML 段落<br>换行后的文字</p>

                <img src="logo.svg" alt="独立图片">

                <!--隐藏的注释-->
                <script>hiddenScript()</script>

                行内 <script>hiddenInlineScript()</script> 正文，`<span>` 和 &lt;em&gt;。

                ---

                <div align='center'>

                **欢迎 Star**

                </div>

                ## 后续内容
                """;
            File.WriteAllText(markdown, markdownSource);
            var markdownWindow = Create(markdown);
            Pump(markdownWindow.LoadPreviewAsync());
            check(markdownWindow.FindName("MarkdownView") is FlowDocumentScrollViewer { Visibility: Visibility.Visible, Document: not null },
                "Markdown 默认打开原生排版视图");
            var markdownView = (FlowDocumentScrollViewer)markdownWindow.FindName("MarkdownView");
            var blocks = markdownView.Document.Blocks;
            var renderedText = new TextRange(markdownView.Document.ContentStart, markdownView.Document.ContentEnd).Text;
            check(blocks.FirstBlock is Paragraph { FontSize: > 20 } && blocks.OfType<Table>().Count() == 1
                && blocks.OfType<System.Windows.Documents.List>().Count() == 1
                && renderedText.Contains("Console.WriteLine") && renderedText.Contains("图片说明"),
                "Markdown 渲染标题、列表、表格和代码块");
            var centered = blocks.OfType<Section>().Where(section => section.TextAlignment == TextAlignment.Center).ToArray();
            check(centered.Length == 2 && centered[0].Blocks.FirstBlock is Paragraph { FontSize: > 20 }
                && centered[1].Blocks.FirstBlock is Paragraph && blocks.LastBlock is Paragraph { TextAlignment: TextAlignment.Left },
                "README 的 HTML 容器居中排版，结束标签后恢复普通排版");
            check(!renderedText.Contains("<div align") && !renderedText.Contains("<p align") && !renderedText.Contains("<img")
                && !renderedText.Contains("隐藏的注释") && !renderedText.Contains("hiddenScript") && !renderedText.Contains("hiddenInlineScript")
                && renderedText.Contains("内嵌图片") && renderedText.Contains("独立图片") && renderedText.Contains("换行后的文字")
                && renderedText.Contains("<div class=\"example\">代码示例</div>") && renderedText.Contains("<span>") && renderedText.Contains("<em>"),
                "排版隐藏 HTML 标签和脚本，保留图片说明、正文和代码示例");
            var firstRow = blocks.OfType<Table>().Single().RowGroups[0].Rows[0];
            check(firstRow.Cells[1].Blocks.FirstBlock is Paragraph { TextAlignment: TextAlignment.Right },
                "Markdown 表格保留列对齐");
            Wheel(markdownWindow, markdownView, 120, shift: true);
            check(markdownView.Zoom == 110, "Markdown 排版支持 Shift 加滚轮缩放");
            var mode = (Button)markdownWindow.FindName("MarkdownModeButton");
            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(markdownView.Visibility == Visibility.Collapsed
                && markdownWindow.FindName("DocumentText") is TextEditor { Text: markdownSource },
                "Markdown 可切换回完整源码");
            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(markdownView.Visibility == Visibility.Visible && markdownView.Zoom == 110, "Markdown 可切回排版并保留缩放比例");
            markdownWindow.RequestClose();
            Pump(markdownWindow.Completion);
            var wideTable = string.Concat(Enumerable.Repeat("| 列 ", 32)) + "|\n"
                + string.Concat(Enumerable.Repeat("| --- ", 32)) + "|";
            check(MarkdownPreview.Parse(wideTable) is null, "超宽 Markdown 表格降级为源码视图");
            check(markdownView.Document is null, "关闭 Markdown 预览释放排版文档");
            var denseMarkdown = Path.Combine(directory, "复杂.md");
            File.WriteAllText(denseMarkdown, string.Concat(Enumerable.Repeat("- [ ] 项目\n", 3_001)));
            markdownWindow = Create(denseMarkdown);
            Pump(markdownWindow.LoadPreviewAsync());
            check(markdownWindow.FindName("DocumentSurface") is FrameworkElement { Visibility: Visibility.Visible }
                && markdownWindow.FindName("MarkdownView") is FlowDocumentScrollViewer { Document: null },
                "结构复杂的 Markdown 降级为源码视图");
            markdownWindow.RequestClose();
            Pump(markdownWindow.Completion);

            foreach (var (name, source, language) in new[] { ("main.go", "package main", "Go"),
                ("lib.rs", "/* nested /* comment */ end */ fn main() {}", "Rust"),
                (".env.production.local", "APP_ENV=production", "Config"), ("config.yml", "enabled: true", "Config"),
                ("Dockerfile", "from scratch", "Dockerfile") })
            {
                var syntax = PreviewSyntax.GetDefinition(name, FilePreview.IsEnvironmentFile(name));
                using var highlighter = new DocumentHighlighter(new TextDocument(source), syntax!);
                check(syntax?.Name == language && highlighter.HighlightLine(1).Sections.Count > 0, $"{name} 实际产生语法高亮");
            }
            var window = Create(pdf);
            Pump(window.LoadPreviewAsync());
            check(((Image)window.FindName("PreviewImage")).Source is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }
                && ((TextBlock)window.FindName("PageLabel")).Text == "1 / 1", "PDF 使用系统渲染器实际加载页面");
            var content = (FrameworkElement)window.Content;
            var size = new Size(960, 720);
            content.Measure(size);
            content.Arrange(new Rect(size));
            content.UpdateLayout();
            var page = (Image)window.FindName("PreviewImage");
            var pageHeight = page.ActualHeight;
            Wheel(window, page, 480, shift: true);
            content.UpdateLayout();
            check(pageHeight > 0 && page.ActualHeight > pageHeight
                && window.FindName("VisualScroll") is ScrollViewer { ScrollableHeight: > 0 }, "PDF 放大后可滚动查看超出窗口的部分");
            var scroller = (ScrollViewer)window.FindName("VisualScroll");
            scroller.ScrollToTop();
            content.UpdateLayout();
            var zoomedHeight = page.ActualHeight;
            Wheel(window, page, -120);
            content.UpdateLayout();
            check(scroller.VerticalOffset > 0 && page.ActualHeight == zoomedHeight, "直接滚轮向下浏览 PDF 并保持缩放比例");
            Wheel(window, page, 120);
            content.UpdateLayout();
            check(scroller.VerticalOffset == 0 && page.ActualHeight == zoomedHeight, "反向滚轮向上浏览 PDF 并保持缩放比例");
            ((Button)window.FindName("ResetZoom")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            content.UpdateLayout();
            check(Math.Abs(page.ActualHeight - pageHeight) < 1
                && ((ScrollViewer)window.FindName("VisualScroll")).ScrollableHeight < 1, "点击缩放比例恢复 PDF 适应窗口");
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
                var path = new[] { image, pdf, text, code }[index % 4];
                var preview = Create(path);
                Pump(preview.LoadPreviewAsync());
                preview.RequestClose();
                preview.RequestClose();
                Pump(preview.Completion);
                using var unlocked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            for (var index = 0; index < 8; index++) Cycle(index);
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
            Console.WriteLine($"预览压力检查：{stressCycles} 次 / {watch.Elapsed.TotalSeconds:F2} s；句柄变化 {handleGrowth}；托管内存变化 {memoryGrowth / 1024d:F0} KB");
            check(handleGrowth < 80 && memoryGrowth < 20_000_000, $"连续 {stressCycles} 次图片、PDF、文字和源码预览无崩溃、文件占用或持续资源增长");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckWorkbook(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "表格.XLSX");
        using (var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create))
        {
            void Write(string name, string xml)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(xml);
            }

            Write("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="汇总" sheetId="1" r:id="rId2"/><sheet name="明细" sheetId="2" r:id="rId1"/></sheets>
                </workbook>
                """);
            Write("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="/xl/worksheets/summary.xml"/>
                </Relationships>
                """);
            Write("xl/sharedStrings.xml", """
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>共享文字</t></si></sst>
                """);
            Write("xl/worksheets/summary.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="2"><c r="A2" t="s"><v>0</v></c><c r="C2" t="inlineStr"><is><t>内联&#10;文字</t></is></c></row>
                  <row r="900"><c r="A900"><v>99</v></c></row>
                </sheetData></worksheet>
                """);
            Write("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1"><f>40+2</f><v>42</v></c><c r="B1" t="b"><v>1</v></c></row>
                </sheetData></worksheet>
                """);
        }

        var window = Create(path);
        Pump(window.LoadPreviewAsync());
        var grid = (DataGrid)window.FindName("WorkbookGrid");
        check(grid.IsReadOnly && ((DockPanel)window.FindName("WorkbookView")).Visibility == Visibility.Visible
            && grid.Items.Count == 2 && grid.Columns.Count == 3
            && Equals(grid.Columns[2].Header, "C")
            && ((WorksheetRow)grid.Items[1])[0] == "共享文字"
            && ((WorksheetRow)grid.Items[1])[1] == ""
            && ((WorksheetRow)grid.Items[1])[2] == "内联\n文字",
            "Excel 用只读表格保留空行、空列和单元格内换行");
        var content = (FrameworkElement)window.Content;
        var size = new Size(960, 720);
        content.Measure(size);
        content.Arrange(new Rect(size));
        content.UpdateLayout();
        check(grid.Columns[2].GetCellContent(grid.Items[1]) is TextBlock { Text: "内联\n文字", TextWrapping: TextWrapping.Wrap },
            "Excel 单元格内容和换行实际绑定到网格");
        var status = (TextBlock)window.FindName("StatusLabel");
        check(status.Text.Contains("部分"), "Excel 超出预览范围时显示提示");
        var picker = (ComboBox)window.FindName("WorksheetPicker");
        check(picker.Items.Cast<WorksheetPreview>().Select(sheet => sheet.Name).SequenceEqual(["汇总", "明细"]),
            "Excel 使用工作簿声明的工作表顺序和名称");
        picker.SelectedIndex = 1;
        check(grid.Items.Count == 1 && grid.Columns.Count == 2
            && ((WorksheetRow)grid.Items[0])[0] == "42" && ((WorksheetRow)grid.Items[0])[1] == "TRUE",
            "Excel 切换工作表并显示公式缓存值和布尔值");
        window.RequestClose();
        Pump(window.Completion);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    private static PreviewWindow Create(string path)
    {
        var preview = new PreviewWindow(path) { ShowInTaskbar = false, ShowActivated = false };
        new WindowInteropHelper(preview).EnsureHandle();
        return preview;
    }

    private static void Wheel(PreviewWindow window, UIElement source, int delta, bool shift = false)
    {
        // Keyboard state is local to this test thread's input queue.
        var previous = new byte[256];
        var keys = new byte[256];
        keys[KeyInterop.VirtualKeyFromKey(Key.LeftShift)] = keys[0x10] = shift ? (byte)0x80 : (byte)0;
        if (!GetKeyboardState(previous) || !SetKeyboardState(keys))
            throw new InvalidOperationException("无法设置滚轮检查的键盘状态。");

        try
        {
            window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = source });
        }
        finally { SetKeyboardState(previous); }
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState([Out] byte[] keys);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keys);

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
