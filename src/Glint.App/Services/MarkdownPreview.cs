using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownRow = Markdig.Extensions.Tables.TableRow;
using MarkdownCell = Markdig.Extensions.Tables.TableCell;
using PipeTableOptions = Markdig.Extensions.Tables.PipeTableOptions;
using TableColumnAlign = Markdig.Extensions.Tables.TableColumnAlign;

namespace Glint.App.Services;

internal static class MarkdownPreview
{
    private const int MaxNodes = 6_000;
    private const int MaxTableColumns = 24;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder { MaximumNestingDepth = 32 }
        .DisableHtml().UsePipeTables(new PipeTableOptions { UseGfmRules = true })
        .UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).Build();
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    private static readonly Brush Ink = ColorBrush(0x20, 0x31, 0x29);
    private static readonly Brush Muted = ColorBrush(0x52, 0x65, 0x5B);
    private static readonly Brush Accent = ColorBrush(0x22, 0x65, 0x47);
    private static readonly Brush Border = ColorBrush(0xD7, 0xE1, 0xD8);
    private static readonly Brush Tint = ColorBrush(0xEF, 0xF4, 0xEF);
    private static readonly Brush Paper = ColorBrush(0xFC, 0xFD, 0xFB);

    public static MarkdownDocument? Parse(string source)
    {
        // Bound potential table width before the parser pads rows with missing cells.
        foreach (var line in source.AsSpan().EnumerateLines())
            if (line.Count('|') > MaxTableColumns + 1) return null;

        MarkdownDocument document;
        try { document = Markdown.Parse(source, Pipeline); }
        catch (ArgumentException)
        {
            // Markdig throws when the input exceeds its parser limits.
            return null;
        }
        return document.Descendants().Take(MaxNodes + 1).Count() > MaxNodes
            || document.Descendants<MarkdownTable>().Any(table => table.ColumnDefinitions.Count > MaxTableColumns) ? null : document;
    }

    public static FlowDocument Render(MarkdownDocument source)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 15, Foreground = Ink, Background = Paper,
            PagePadding = new Thickness(30, 22, 30, 28), MaxPageWidth = 800,
            IsHyphenationEnabled = false
        };
        AddBlocks(source, document.Blocks);
        return document;
    }

    private static void AddBlocks(ContainerBlock source, BlockCollection target)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = Paragraph(heading.Inline);
                    title.FontSize = heading.Level switch { 1 => 30, 2 => 23, 3 => 19, _ => 16 };
                    title.FontWeight = FontWeights.SemiBold;
                    title.Margin = new Thickness(0, target.Count == 0 ? 0 : 16, 0, 10);
                    if (heading.Level <= 2)
                    {
                        title.BorderBrush = Border;
                        title.BorderThickness = new Thickness(0, 0, 0, 1);
                        title.Padding = new Thickness(0, 0, 0, 7);
                    }
                    target.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    target.Add(Paragraph(paragraph.Inline));
                    break;
                case CodeBlock code:
                    var codeText = new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = Mono, FontSize = 13, Background = Tint, BorderBrush = Border,
                        BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = new Thickness(0, 2, 0, 13)
                    };
                    target.Add(codeText);
                    break;
                case QuoteBlock quote:
                    var section = new Section
                    {
                        Foreground = Muted, Background = Tint, BorderBrush = Accent,
                        BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 7, 10, 0),
                        Margin = new Thickness(0, 0, 0, 12)
                    };
                    AddBlocks(quote, section.Blocks);
                    target.Add(section);
                    break;
                case ListBlock list:
                    var items = new List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Padding = new Thickness(23, 0, 0, 0), Margin = new Thickness(0, 0, 0, 8)
                    };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
                        items.StartIndex = Math.Max(1, start);
                    foreach (var entry in list.OfType<ListItemBlock>())
                    {
                        var item = new ListItem();
                        AddBlocks(entry, item.Blocks);
                        foreach (var line in item.Blocks.OfType<Paragraph>()) line.Margin = new Thickness(0, 0, 0, 4);
                        items.ListItems.Add(item);
                    }
                    target.Add(items);
                    break;
                case MarkdownTable table:
                    target.Add(RenderTable(table));
                    break;
                case ThematicBreakBlock:
                    target.Add(new Paragraph
                    {
                        FontSize = 1, LineHeight = 1, BorderBrush = Border, BorderThickness = new Thickness(0, 0, 0, 1),
                        Margin = new Thickness(0, 7, 0, 12)
                    });
                    break;
            }
        }
    }

    private static Table RenderTable(MarkdownTable source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 14) };
        var rows = new TableRowGroup();
        table.RowGroups.Add(rows);
        foreach (var row in source.OfType<MarkdownRow>())
        {
            var output = new TableRow { Background = row.IsHeader ? Tint : Brushes.Transparent };
            foreach (var cell in row.OfType<MarkdownCell>())
            {
                var value = new TableCell
                {
                    Padding = new Thickness(9, 5, 9, 5), BorderBrush = Border, BorderThickness = new Thickness(0, 0, 1, 1),
                    FontWeight = row.IsHeader ? FontWeights.SemiBold : FontWeights.Normal
                };
                AddBlocks(cell, value.Blocks);
                var alignment = source.ColumnDefinitions[output.Cells.Count].Alignment switch
                {
                    TableColumnAlign.Center => TextAlignment.Center,
                    TableColumnAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                };
                foreach (var paragraph in value.Blocks.OfType<Paragraph>())
                {
                    paragraph.Margin = new Thickness(0);
                    paragraph.TextAlignment = alignment;
                }
                output.Cells.Add(value);
            }
            rows.Rows.Add(output);
        }
        table.BorderBrush = Border;
        table.BorderThickness = new Thickness(1, 1, 0, 0);
        return table;
    }

    private static Paragraph Paragraph(ContainerInline? source)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        AddInlines(source, paragraph.Inlines);
        return paragraph;
    }

    private static void AddInlines(ContainerInline? source, InlineCollection target)
    {
        for (var inline = source?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline text: target.Add(new Run(text.Content.ToString())); break;
                case HtmlEntityInline entity: target.Add(new Run(entity.Transcoded.ToString())); break;
                case LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = Mono, Background = Tint, Foreground = Accent }); break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ") { Foreground = Accent }); break;
                case AutolinkInline link: target.Add(new Run(link.Url) { Foreground = Accent }); break;
                case LinkInline link:
                    var label = new Span { Foreground = Accent, ToolTip = link.Url };
                    if (link.IsImage) label.Inlines.Add(new Run("图片 · "));
                    else label.TextDecorations = TextDecorations.Underline;
                    AddInlines(link, label.Inlines);
                    if (label.Inlines.Count == (link.IsImage ? 1 : 0) && link.Url is { } url) label.Inlines.Add(new Run(url));
                    target.Add(label);
                    break;
                case EmphasisInline emphasis:
                    Span styled = emphasis.DelimiterChar == '~'
                        ? new Span { TextDecorations = TextDecorations.Strikethrough }
                        : emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    AddInlines(emphasis, styled.Inlines);
                    target.Add(styled);
                    break;
                case ContainerInline container: AddInlines(container, target); break;
            }
        }
    }

    private static Brush ColorBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
