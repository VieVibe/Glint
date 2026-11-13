using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Glint.Core;

public enum FilePreviewKind { Unsupported, Image, Video, Text, Pdf, Office }

public sealed record DocumentPreview(string Text, bool Truncated, string Format);

public static class FilePreview
{
    public const int MaxTextCharacters = 200_000;
    private const int MaxXmlCharacters = 4_000_000;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".heic", ".heif", ".avif", ".wdp" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".adoc", ".csv", ".tsv", ".log",
        ".cs", ".fs", ".fsx", ".fsi", ".vb", ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx", ".inl",
        ".go", ".rs", ".java", ".kt", ".kts", ".scala", ".swift", ".dart", ".lua", ".rb", ".php", ".py", ".pyi", ".pyw", ".r",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".vue", ".svelte", ".astro",
        ".html", ".htm", ".css", ".scss", ".sass", ".less", ".svg", ".razor", ".cshtml",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish", ".sql", ".graphql", ".gql", ".proto",
        ".json", ".jsonc", ".json5", ".jsonl", ".ndjson", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".properties",
        ".editorconfig", ".env", ".config", ".manifest", ".xaml", ".axaml", ".xsd", ".xsl", ".xslt", ".plist", ".resx",
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".vcxproj", ".proj", ".props", ".targets", ".nuspec",
        ".cmake", ".mk", ".gradle", ".groovy", ".dockerfile", ".tf", ".tfvars", ".hcl", ".diff", ".patch", ".gitignore", ".gitattributes"
    };
    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "CMakeLists.txt", "Jenkinsfile", "Vagrantfile",
        "Gemfile", "Rakefile", "Brewfile", "Podfile", "Pipfile", "Pipfile.lock", "Cargo.lock", "yarn.lock", "poetry.lock", "uv.lock",
        "composer.lock", "go.mod", "go.sum", "go.work", "gradle.lockfile", "Package.resolved", "flake.lock",
        ".dockerignore", ".npmignore", ".npmrc", ".yarnrc", ".gitmodules", ".gitconfig", ".hgignore", ".prettierignore", ".eslintignore",
        "LICENSE", "LICENCE", "README", "CHANGELOG", "COPYING", "NOTICE"
    };
    private static readonly HashSet<string> EnvironmentFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".env.dev", ".env.development", ".env.test", ".env.testing", ".env.prod", ".env.production", ".env.staging",
        ".env.preview", ".env.ci", ".env.qa", ".env.uat", ".env.example", ".env.sample", ".env.template", ".env.dist", ".env.defaults"
    };

    public static FilePreviewKind GetKind(string path)
    {
        var extension = Path.GetExtension(path);
        if (ImageExtensions.Contains(extension)) return FilePreviewKind.Image;
        if (VideoExtensions.Contains(extension)) return FilePreviewKind.Video;
        if (TextExtensions.Contains(extension) || TextFileNames.Contains(Path.GetFileName(path)) || IsEnvironmentFile(path))
            return FilePreviewKind.Text;
        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Pdf;
        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Office;
        return FilePreviewKind.Unsupported;
    }

    public static bool IsEnvironmentFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        return EnvironmentFileNames.Contains(name);
    }

    public static async Task<DocumentPreview> ReadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        if (GetKind(path) == FilePreviewKind.Office)
            return await Task.Run(() => ReadOffice(path, cancellationToken), cancellationToken);
        if (GetKind(path) != FilePreviewKind.Text) throw new NotSupportedException("此格式暂不支持文字预览。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxTextCharacters + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        var text = new string(buffer, 0, Math.Min(count, MaxTextCharacters));
        if (text.Contains('\0')) throw new InvalidDataException("文件包含二进制内容，无法作为文字预览。");
        return new(text, count > MaxTextCharacters, "文字预览");
    }

    private static DocumentPreview ReadOffice(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 64_000_000) throw new InvalidDataException("文档文件过大，请使用默认应用打开。");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count > 4096) throw new InvalidDataException("文档内容过多，请使用默认应用打开。");
        var output = new StringBuilder();
        var truncated = false;
        long xmlBudget = 16_000_000;
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";

        XDocument ReadXml(ZipArchiveEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            xmlBudget -= entry.Length;
            if (entry.Length > MaxXmlCharacters || xmlBudget < 0) throw new InvalidDataException("文档内容过大，请使用默认应用打开。");
            using var input = entry.Open();
            using var xml = XmlReader.Create(input, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlCharacters });
            return XDocument.Load(xml);
        }

        void Append(string value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = MaxTextCharacters - output.Length;
            if (value.Length > remaining) truncated = true;
            if (remaining > 0) output.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".docx")
        {
            var entry = archive.GetEntry("word/document.xml") ?? throw new InvalidDataException("Word 文档内容缺失。");
            foreach (var paragraph in ReadXml(entry).Descendants(word + "p"))
            {
                foreach (var node in paragraph.Descendants())
                {
                    if (node.Name == word + "t") Append(node.Value);
                    else if (node.Name == word + "tab") Append("\t");
                    else if (node.Name == word + "br" || node.Name == word + "cr") Append("\n");
                }
                Append("\n");
                if (truncated) break;
            }
        }
        else if (extension == ".pptx")
        {
            var entries = NumberedEntries(archive, "ppt/slides/slide");
            if (entries.Length == 0) throw new InvalidDataException("PowerPoint 文档内容缺失。");
            foreach (var entry in entries)
            {
                Append($"── 幻灯片 {EntryNumber(entry)} ──\n\n");
                foreach (var paragraph in ReadXml(entry).Descendants(drawing + "p"))
                    Append(string.Concat(paragraph.Descendants(drawing + "t").Select(node => node.Value)) + "\n");
                Append("\n");
                if (truncated) break;
            }
        }
        else
        {
            var sharedEntry = archive.GetEntry("xl/sharedStrings.xml");
            var shared = sharedEntry is null ? [] : ReadXml(sharedEntry).Descendants(sheet + "si")
                .Select(item => string.Concat(item.Descendants(sheet + "t").Select(node => node.Value))).ToArray();
            var entries = NumberedEntries(archive, "xl/worksheets/sheet");
            if (entries.Length == 0) throw new InvalidDataException("Excel 文档内容缺失。");
            foreach (var entry in entries)
            {
                Append($"── 工作表 {EntryNumber(entry)} ──\n\n");
                foreach (var row in ReadXml(entry).Descendants(sheet + "row"))
                {
                    var column = 0;
                    foreach (var cell in row.Elements(sheet + "c"))
                    {
                        var reference = (string?)cell.Attribute("r") ?? "";
                        var targetColumn = 0;
                        foreach (var letter in reference.TakeWhile(char.IsAsciiLetterUpper).Take(4)) targetColumn = targetColumn * 26 + letter - 'A' + 1;
                        // Keep empty columns visible without allowing huge sparse rows to allocate unbounded text.
                        var gapEnd = Math.Min(targetColumn - 1, column + 100);
                        while (column < gapEnd && !truncated) { Append("\t"); column++; }
                        var value = (string?)cell.Element(sheet + "v") ?? "";
                        var type = (string?)cell.Attribute("t");
                        if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length) value = shared[index];
                        else if (type == "inlineStr") value = string.Concat(cell.Descendants(sheet + "t").Select(node => node.Value));
                        else if (value.Length == 0 && cell.Element(sheet + "f") is { } formula) value = "=" + formula.Value;
                        Append(value + "\t");
                        column++;
                        if (truncated) break;
                    }
                    Append("\n");
                    if (truncated) break;
                }
                Append("\n");
                if (truncated) break;
            }
        }
        return new(output.ToString(), truncated, "Office 文字预览 · 不含原始排版和嵌入图片");
    }

    private static ZipArchiveEntry[] NumberedEntries(ZipArchive archive, string prefix) => archive.Entries
        .Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
            && entry.FullName.EndsWith(".xml", StringComparison.Ordinal)
            && int.TryParse(entry.FullName.AsSpan(prefix.Length, entry.FullName.Length - prefix.Length - 4), out _))
        .OrderBy(EntryNumber).ToArray();

    private static int EntryNumber(ZipArchiveEntry entry) => int.Parse(new string(Path.GetFileNameWithoutExtension(entry.Name).Where(char.IsAsciiDigit).ToArray()));
}
