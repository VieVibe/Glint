using System.Text.RegularExpressions;
using System.Windows;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax;

namespace Glint.App.Services;

internal static partial class MarkdownHtml
{
    internal sealed class StandaloneTag(BlockParser parser, string tag) : LeafBlock(parser)
    {
        public string Tag { get; } = tag;
    }

    internal sealed class StandaloneTagParser : HtmlBlockParser
    {
        public override BlockState TryOpen(BlockProcessor processor)
        {
            var line = processor.Line;
            if (!processor.IsCodeIndent && HtmlHelper.TryParseHtmlTag(ref line, out var tag)
                && line.AsSpan().Trim().IsEmpty && (IsContainer(Name(tag)) || Name(tag) is "img" or "br" or "hr"))
            {
                processor.NewBlocks.Push(new StandaloneTag(this, tag)
                {
                    Column = processor.Column,
                    Span = new SourceSpan(processor.Start, processor.Line.End)
                });
                return BlockState.BreakDiscard;
            }
            return base.TryOpen(processor);
        }
    }

    public static string Name(string tag)
    {
        var start = tag.StartsWith("</", StringComparison.Ordinal) ? 2 : 1;
        var end = start;
        while (end < tag.Length && (char.IsAsciiLetterOrDigit(tag[end]) || tag[end] == '-')) end++;
        return tag[start..end].ToLowerInvariant();
    }

    public static bool IsContainer(string name) => name is "div" or "p" or "center" or "section" or "article";

    public static string? Attribute(string tag, string name)
    {
        var start = 1 + Name(tag).Length;
        foreach (Match attribute in Attributes().Matches(tag, start))
        {
            if (!attribute.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return System.Net.WebUtility.HtmlDecode(attribute.Groups[2].Value.Trim('"', '\''));
        }
        return null;
    }

    public static TextAlignment? Alignment(string tag) => Attribute(tag, "align")?.ToLowerInvariant() switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        "justify" => TextAlignment.Justify,
        "left" => TextAlignment.Left,
        _ => Name(tag) == "center" ? TextAlignment.Center : null
    };

    public static bool IsHidden(string name) => name is "script" or "style" or "iframe" or "object" or "template" or "head";

    [GeneratedRegex("""\s+([\w:.-]+)(?:\s*=\s*("[^"]*"|'[^']*'|[^\s"'=<>`]+))?""", RegexOptions.NonBacktracking)]
    private static partial Regex Attributes();
}
