using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;

namespace HagoromoChan.Desktop;

// A deliberately limited presentation projection. It never produces HTML,
// navigable links, images, a resource URI, or a new stored message body.
internal static class HistoryMarkdown
{
    private const int MaximumFormattedLength = 32_768;
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().UsePreciseSourceLocation().Build();
    private static readonly FontFamily CodeFont = new("Consolas, Yu Gothic UI, Meiryo");

    public static FlowDocument? TryCreateDocument(string source)
    {
        if (source.Length is 0 or > MaximumFormattedLength)
            return null;
        try
        {
            var tree = Markdown.Parse(source, Pipeline);
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            var styled = false;
            foreach (var block in tree)
                document.Blocks.Add(ConvertBlock(block, source, ref styled));
            return styled ? document : null;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            // If any structure cannot be rendered accurately, keep the
            // original selectable in its existing plain history control.
            return null;
        }
    }

    private static WpfBlock ConvertBlock(MarkdownBlock block, string source, ref bool styled)
    {
        switch (block)
        {
            case HeadingBlock heading:
                styled = true;
                var headingParagraph = MakeParagraph(heading.Inline, source, ref styled);
                headingParagraph.FontWeight = FontWeights.Bold;
                headingParagraph.FontSize = heading.Level switch { 1 => 19, 2 => 17, _ => 15 };
                return headingParagraph;
            case ParagraphBlock paragraph:
                return MakeParagraph(paragraph.Inline, source, ref styled);
            case FencedCodeBlock code:
                styled = true;
                return new Paragraph(new Run(code.Lines.ToString()))
                {
                    FontFamily = CodeFont, Background = Brushes.WhiteSmoke, Margin = new Thickness(0, 0, 0, 5)
                };
            case QuoteBlock quote:
                styled = true;
                var section = new Section { BorderBrush = Brushes.LightSlateGray,
                    BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 0, 0, 0),
                    Margin = new Thickness(0, 0, 0, 4) };
                foreach (var inner in quote)
                    section.Blocks.Add(ConvertBlock(inner, source, ref styled));
                return section;
            case ListBlock list:
                styled = true;
                var rendered = new WpfList
                {
                    MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    StartIndex = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0 ? start : 1,
                    Padding = new Thickness(18, 0, 0, 0), Margin = new Thickness(0, 0, 0, 3)
                };
                foreach (var inner in list)
                {
                    if (inner is not ListItemBlock item) throw new NotSupportedException();
                    var entry = new ListItem();
                    foreach (var paragraphBlock in item)
                        entry.Blocks.Add(ConvertBlock(paragraphBlock, source, ref styled));
                    rendered.ListItems.Add(entry);
                }
                return rendered;
            default:
                throw new NotSupportedException();
        }
    }

    private static Paragraph MakeParagraph(ContainerInline? contents, string source, ref bool styled)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
        Append(paragraph.Inlines, contents, source, ref styled);
        return paragraph;
    }

    private static void Append(InlineCollection output, ContainerInline? contents, string source, ref bool styled)
    {
        for (var token = contents?.FirstChild; token is not null; token = token.NextSibling)
        {
            WpfInline element;
            switch (token)
            {
                case LiteralInline literal:
                    element = new Run(literal.Content.ToString());
                    break;
                case LineBreakInline lineBreak:
                    element = lineBreak.IsHard ? new LineBreak() : new Run(" ");
                    break;
                case CodeInline code:
                    styled = true;
                    element = new Run(code.Content) { FontFamily = CodeFont, Background = Brushes.WhiteSmoke };
                    break;
                case EmphasisInline { DelimiterChar: '*' or '_' } emphasis:
                    styled = true;
                    Span span = emphasis.DelimiterCount == 2 ? new Bold() : new Italic();
                    Append(span.Inlines, emphasis, source, ref styled);
                    element = span;
                    break;
                case LinkInline or AutolinkInline:
                    // Keep the destination and image syntax literal. No
                    // Hyperlink, NavigateUri, Image, or network access.
                    element = new Run(Original(source, token));
                    break;
                default:
                    throw new NotSupportedException();
            }
            output.Add(element);
        }
    }

    private static string Original(string source, MarkdownInline token)
    {
        if (token.Span.Start < 0 || token.Span.End < token.Span.Start || token.Span.End >= source.Length)
            throw new NotSupportedException();
        return source[token.Span.Start..(token.Span.End + 1)];
    }
}

internal sealed class ConversationRichTextBox : RichTextBox
{
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_ContentHost") is ScrollViewer host)
            host.CanContentScroll = false;
    }
}
