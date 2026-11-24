using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace Glint.Core;

public sealed record WorkbookPreview(IReadOnlyList<WorksheetPreview> Sheets, bool Truncated);
public sealed record WorksheetPreview(string Name, IReadOnlyList<WorksheetRow> Rows, int ColumnCount, bool Truncated);

public sealed class WorksheetRow(int number)
{
    private readonly Dictionary<int, string> cells = [];
    public int Number { get; } = number;
    public string this[int column] => cells.GetValueOrDefault(column, "");
    internal void SetCell(int column, string value) => cells[column] = value;
}

public static partial class FilePreview
{
    private const int MaxWorksheetRows = 500;
    private const int MaxWorksheetColumns = 64;
    private const int MaxWorkbookSheets = 64;
    private const int MaxCellCharacters = 2000;

    public static Task<WorkbookPreview> ReadWorkbookAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadOfficePackage(path, cancellationToken,
            (archive, readXml) => ReadWorkbook(archive, readXml, cancellationToken)), cancellationToken);

    private static WorkbookPreview ReadWorkbook(ZipArchive archive, Func<ZipArchiveEntry, XDocument> readXml,
        CancellationToken cancellationToken)
    {
        XDocument ReadPart(string name) => readXml(archive.GetEntry(name)
            ?? throw new InvalidDataException("Excel 工作簿内容缺失。"));

        var workbook = ReadPart("xl/workbook.xml").Root!;
        var sheetNamespace = workbook.Name.Namespace;
        XNamespace relationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace strictRelationshipNamespace = "http://purl.oclc.org/ooxml/officeDocument/relationships";
        var relationships = ReadPart("xl/_rels/workbook.xml.rels").Root!.Elements()
            .Where(part => part.Name.LocalName == "Relationship" && (string?)part.Attribute("TargetMode") != "External")
            .ToDictionary(part => (string)part.Attribute("Id")!);
        var sharedPart = relationships.Values.FirstOrDefault(part => HasRelationshipType(part, "sharedStrings"));
        var shared = ReadSharedStrings(sharedPart is null ? archive.GetEntry("xl/sharedStrings.xml")
            : archive.GetEntry(WorkbookPartName(sharedPart)), readXml);
        var sheets = new List<WorksheetPreview>();
        var truncated = false;
        foreach (var sheet in workbook.Element(sheetNamespace + "sheets")?.Elements(sheetNamespace + "sheet") ?? [])
        {
            var id = (string?)(sheet.Attribute(relationshipNamespace + "id") ?? sheet.Attribute(strictRelationshipNamespace + "id"));
            if (id is null || !relationships.TryGetValue(id, out var relation) || !HasRelationshipType(relation, "worksheet"))
                continue;

            if (sheets.Count == MaxWorkbookSheets)
            {
                truncated = true;
                break;
            }
            var name = (string?)sheet.Attribute("name") ?? $"工作表 {sheets.Count + 1}";
            sheets.Add(ReadWorksheet(name, ReadPart(WorkbookPartName(relation)).Root!, shared, cancellationToken));
        }
        if (sheets.Count == 0) throw new InvalidDataException("Excel 工作簿没有可预览的工作表。");
        return new(sheets, truncated);
    }

    private static bool HasRelationshipType(XElement relation, string type) =>
        ((string?)relation.Attribute("Type"))?.EndsWith("/" + type, StringComparison.Ordinal) == true;

    private static string WorkbookPartName(XElement relation)
    {
        var root = new Uri("https://workbook.invalid/xl/workbook.xml");
        var target = (string?)relation.Attribute("Target");
        if (string.IsNullOrEmpty(target) || !Uri.TryCreate(root, target, out var part) || part.Host != root.Host
            || part.Scheme != root.Scheme || part.Query.Length != 0 || part.Fragment.Length != 0)
            throw new InvalidDataException("Excel 工作簿部件路径无效。");
        return Uri.UnescapeDataString(part.AbsolutePath[1..]);
    }

    private static WorksheetPreview ReadWorksheet(string name, XElement root, string[] shared,
        CancellationToken cancellationToken)
    {
        var ns = root.Name.Namespace;
        var rows = new List<WorksheetRow>();
        var rowNumber = 0;
        var columnCount = 0;
        var truncated = false;
        var remaining = MaxTextCharacters;
        foreach (var row in root.Element(ns + "sheetData")?.Elements(ns + "row") ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber = int.TryParse((string?)row.Attribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index > rowNumber ? index : rowNumber + 1;
            if (rowNumber > MaxWorksheetRows)
            {
                truncated = true;
                break;
            }

            while (rows.Count < rowNumber) rows.Add(new(rows.Count + 1));
            var column = 0;
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? "";
                var target = 0;
                foreach (var letter in reference)
                {
                    if (!char.IsAsciiLetterUpper(letter) || target > MaxWorksheetColumns) break;
                    target = target * 26 + letter - 'A' + 1;
                }
                column = target > 0 ? target : column + 1;
                if (column > MaxWorksheetColumns)
                {
                    truncated = true;
                    continue;
                }

                columnCount = Math.Max(columnCount, column);
                var value = ReadCell(cell, shared);
                if ((string?)cell.Attribute("t") == "b") value = value switch { "1" => "TRUE", "0" => "FALSE", _ => value };
                var length = Math.Min(value.Length, Math.Min(remaining, MaxCellCharacters));
                if (length < value.Length) truncated = true;
                if (length > 0)
                {
                    rows[^1].SetCell(column - 1, value[..length]);
                    remaining -= length;
                }
            }
        }
        return new(name, rows, columnCount, truncated);
    }

    private static string[] ReadSharedStrings(ZipArchiveEntry? part, Func<ZipArchiveEntry, XDocument> readXml)
    {
        if (part is null) return [];
        var root = readXml(part).Root!;
        var ns = root.Name.Namespace;
        return root.Elements(ns + "si").Select(item => ReadRichText(item, ns)).ToArray();
    }

    private static string ReadRichText(XElement item, XNamespace ns) =>
        string.Concat(item.Elements().Select(node => node.Name == ns + "t" ? node.Value
            : node.Name == ns + "r" ? (string?)node.Element(ns + "t") : null));

    private static string ReadCell(XElement cell, string[] shared)
    {
        var ns = cell.Name.Namespace;
        var value = (string?)cell.Element(ns + "v") ?? "";
        var type = (string?)cell.Attribute("t");
        if (type == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length) return shared[index];
        if (type == "inlineStr" && cell.Element(ns + "is") is { } inline) return ReadRichText(inline, ns);
        if (value.Length == 0 && cell.Element(ns + "f") is { } formula) return "=" + formula.Value;
        return value;
    }
}
