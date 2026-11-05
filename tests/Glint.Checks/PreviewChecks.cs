using System.IO;
using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using System.Xml;
using Glint.App.Services;
using Glint.Core;

internal static class PreviewChecks
{
    public static async Task InspectExplorerAsync()
    {
        var foreground = ExplorerSelection.GetForegroundWindow();
        Console.WriteLine($"Foreground: {foreground} ({GetWindowClass(foreground)})");
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        GetGUIThreadInfo(GetWindowThreadProcessId(foreground, out _), ref info);
        Console.WriteLine($"GUI flags: {info.Flags}; focus ancestry:");
        for (var focus = info.Focus; focus != IntPtr.Zero; focus = GetParent(focus))
            Console.WriteLine($"  {focus} ({GetWindowClass(focus)})");
        if (ExplorerSelection.TryGetContext(out var context))
            Console.WriteLine($"File view: {context}; selected file: {await ExplorerSelection.GetSelectedFileAsync(context, Console.WriteLine)}");
        else Console.WriteLine("No eligible Explorer file view has keyboard focus.");
    }

    public static async Task RunAsync(string directory, Action<bool, string> check)
    {
        check(PreviewLimits.Fit(100_000, 1).Width <= 2400 && PreviewLimits.Fit(1, 100_000).Height <= 2400
            && PreviewLimits.Fit(100_000, 100_000, 1600, 2200) == (1600, 1600), "极端图片和 PDF 渲染尺寸保持有界");
        var invalidSize = false;
        try { PreviewLimits.Fit(double.PositiveInfinity, 10); } catch (ArgumentOutOfRangeException) { invalidSize = true; }
        check(invalidSize, "无效的页面尺寸被拒绝");
        check(FilePreview.GetKind("照片.JpG") == FilePreviewKind.Image && FilePreview.GetKind("视频.MP4") == FilePreviewKind.Video
            && FilePreview.GetKind("文档.PDF") == FilePreviewKind.Pdf && FilePreview.GetKind("代码.ts") == FilePreviewKind.Text, "预览格式识别、大小写与代码文件");
        check(FilePreview.GetKind("程序.exe") == FilePreviewKind.Unsupported && FilePreview.GetKind("旧文档.doc") == FilePreviewKind.Unsupported,
            "不支持的预览格式不会当作文字读取");
        var path = Path.Combine(directory, "中文文字.txt");
        await File.WriteAllTextAsync(path, "空格预览\n第二行", Encoding.UTF8);
        var text = await FilePreview.ReadDocumentAsync(path);
        check(text.Text == "空格预览\n第二行" && !text.Truncated, "UTF-8 中文文档预览");
        await File.WriteAllTextAsync(path, "中文 UTF-16 预览", Encoding.Unicode);
        check((await FilePreview.ReadDocumentAsync(path)).Text == "中文 UTF-16 预览", "带 BOM 的 UTF-16 文档预览");
        await File.WriteAllTextAsync(path, new string('长', FilePreview.MaxTextCharacters + 100));
        text = await FilePreview.ReadDocumentAsync(path);
        check(text.Text.Length == FilePreview.MaxTextCharacters && text.Truncated, "大文档预览有界读取并标记截断");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var aborted = false;
            try { await FilePreview.ReadDocumentAsync(path, cancelled.Token); }
            catch (OperationCanceledException) { aborted = true; }
            check(aborted, "关闭预览可取消文档加载");
        }
        await File.WriteAllTextAsync(path, "binary\0content");
        var binaryRejected = false;
        try { await FilePreview.ReadDocumentAsync(path); } catch (InvalidDataException) { binaryRejected = true; }
        check(binaryRejected, "二进制伪装为文字文件时安全失败");

        var word = Path.Combine(directory, "测试.docx");
        WriteArchive(word, ("word/document.xml", "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>中文 Word</w:t><w:tab/><w:t>内容</w:t><w:br/><w:t>下一行</w:t></w:r></w:p></w:body></w:document>"));
        check((await FilePreview.ReadDocumentAsync(word)).Text == "中文 Word\t内容\n下一行\n", "Word 文字、制表符和换行预览");
        var excel = Path.Combine(directory, "测试.xlsx");
        WriteArchive(excel,
            ("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><r><t>共享</t></r><r><t>文字</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"C1\"><v>42</v></c><c r=\"D1\" t=\"inlineStr\"><is><t>内联文字</t></is></c></row></sheetData></worksheet>"));
        check((await FilePreview.ReadDocumentAsync(excel)).Text.Contains("共享文字\t\t42\t内联文字"), "Excel 共享文字、空列与内联文字预览");
        var slides = Path.Combine(directory, "测试.pptx");
        WriteArchive(slides,
            ("ppt/slides/slide10.xml", "<a:p xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:r><a:t>第十页</a:t></a:r></a:p>"),
            ("ppt/slides/slide2.xml", "<a:p xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:r><a:t>第二页</a:t></a:r></a:p>"));
        text = await FilePreview.ReadDocumentAsync(slides);
        check(text.Text.IndexOf("第二页", StringComparison.Ordinal) < text.Text.IndexOf("第十页", StringComparison.Ordinal), "PowerPoint 幻灯片按数字顺序预览");
        using (new FileStream(word, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        check(true, "读取结束立即释放 Office 文件句柄");
        WriteArchive(word, ("word/document.xml", "<!DOCTYPE document [<!ENTITY x SYSTEM 'file:///not-read'>]><document>&x;</document>"));
        var dtdRejected = false;
        try { await FilePreview.ReadDocumentAsync(word); } catch (XmlException) { dtdRejected = true; }
        check(dtdRejected, "Office 预览禁止 XML 外部实体");
        WriteArchive(word, ("word/document.xml", new string('x', 4_000_001)));
        var oversizedRejected = false;
        try { await FilePreview.ReadDocumentAsync(word); } catch (InvalidDataException) { oversizedRejected = true; }
        check(oversizedRejected, "Office 压缩内容过大时停止预览");
        await File.WriteAllTextAsync(word, "broken zip");
        var corruptRejected = false;
        try { await FilePreview.ReadDocumentAsync(word); } catch (InvalidDataException) { corruptRejected = true; }
        check(corruptRejected, "损坏的 Office 文档可检测");
        using (var archive = new ZipArchive(File.Create(word), ZipArchiveMode.Create))
            for (var index = 0; index < 4097; index++) archive.CreateEntry($"part{index}");
        var manyPartsRejected = false;
        try { await FilePreview.ReadDocumentAsync(word); } catch (InvalidDataException) { manyPartsRejected = true; }
        check(manyPartsRejected, "异常多文件的 Office 压缩包被拒绝");
    }

    private static void WriteArchive(string path, params (string Name, string Xml)[] entries)
    {
        using var archive = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open(), Encoding.UTF8);
            writer.Write(entry.Xml);
        }
    }

    private static string GetWindowClass(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public int Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
}
