using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace HagoromoChan.Desktop;

internal sealed record HistoryBodyMeasurement(double FullHeight, HistoryExcerpt[] Excerpts)
{
    public bool CanCollapse => Excerpts.Length != 0;
    public HistoryExcerpt Preview => Excerpts[^1];

    public static HistoryBodyMeasurement Measure(TextBox probe, string text, double width, DpiScale dpi, int lines)
    {
        VisualTreeHelper.SetRootDpi(probe, dpi);
        probe.Width = probe.MaxWidth = width;
        var space = new Size(width, double.PositiveInfinity);
        probe.Text = text;
        probe.Measure(space);
        var full = probe.DesiredSize.Height;

        // Ask WPF's text formatter for actual wrap break positions. Unlike
        // TextBox character-at-line helpers this doesn't need an arranged native
        // editor or use a guessed pixel point near the line's midpoint.
        probe.ApplyTemplate();
        var editor = (probe.Template.FindName("PART_ContentHost", probe) as ScrollViewer)?.Content as FrameworkElement;
        var gutter = editor is null ? 0 : editor.Margin.Left + editor.Margin.Right;
        var available = Math.Max(1, width - gutter - probe.Padding.Left - probe.Padding.Right -
            probe.BorderThickness.Left - probe.BorderThickness.Right);
        var run = new RunStyle(probe) { PixelsPerDip = dpi.PixelsPerDip };
        var source = new HistoryTextSource(text, run) { PixelsPerDip = dpi.PixelsPerDip };
        var paragraph = new ParagraphStyle(probe, run);
        var excerpts = new List<HistoryExcerpt>(lines);
        using var formatter = TextFormatter.Create(TextOptions.GetTextFormattingMode(probe));
        TextLineBreak? continuation = null;
        var index = 0;
        try
        {
            for (var count = 0; count <= lines; count++)
            {
                using var line = formatter.FormatLine(source, index, available, paragraph, continuation);
                continuation?.Dispose();
                continuation = null;
                if (count == lines) return new(full, excerpts.ToArray());
                if (line.Length <= 0) break;
                var end = index + line.Length;
                if (end > text.Length || end == text.Length && line.NewlineLength == 0) break;
                var visibleEnd = end;
                if (visibleEnd > index && text[visibleEnd - 1] == '\n') visibleEnd--;
                if (visibleEnd > index && text[visibleEnd - 1] == '\r') visibleEnd--;
                var excerpt = text[..visibleEnd];
                probe.Text = excerpt;
                probe.Measure(space);
                excerpts.Add(new(excerpt, probe.DesiredSize.Height));
                index = end;
                continuation = line.GetTextLineBreak();
            }
        }
        finally { continuation?.Dispose(); }
        return new(full, []);
    }

    private sealed class HistoryTextSource(string text, RunStyle style) : TextSource
    {
        public override TextRun GetTextRun(int index)
        {
            if (index >= text.Length) return new TextEndOfParagraph(1);
            return new TextCharacters(text, index, text.Length - index, style);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit)
        {
            var length = Math.Min(limit, text.Length);
            return new(length, new CultureSpecificCharacterBufferRange(style.CultureInfo,
                new CharacterBufferRange(text, 0, length)));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class RunStyle(TextBox probe) : TextRunProperties
    {
        public override Typeface Typeface { get; } = new(probe.FontFamily, probe.FontStyle, probe.FontWeight, probe.FontStretch);
        public override double FontRenderingEmSize => probe.FontSize;
        public override double FontHintingEmSize => probe.FontSize;
        public override TextDecorationCollection TextDecorations => probe.TextDecorations;
        public override Brush ForegroundBrush => probe.Foreground;
        public override Brush BackgroundBrush => null!;
        public override CultureInfo CultureInfo { get; } = probe.Language.GetEquivalentCulture();
        public override TextEffectCollection TextEffects => null!;
    }

    private sealed class ParagraphStyle(TextBox probe, RunStyle run) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => probe.FlowDirection;
        public override TextAlignment TextAlignment => probe.TextAlignment;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => false;
        public override TextRunProperties DefaultTextRunProperties => run;
        public override TextWrapping TextWrapping => probe.TextWrapping;
        public override TextMarkerProperties TextMarkerProperties => null!;
        public override double Indent => 0;
    }
}

internal sealed record HistoryExcerpt(string Text, double Height);
