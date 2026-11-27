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

    // WPF's TextBoxView reserves CaretElement.BidiCaretIndicatorWidth on
    // each side of the line box. Match that layout input without reading a
    // control theme/template that may not exist before the first HWND layout.
    private const double NativeCaretGutter = 2.0;

    public static HistoryBodyMeasurement Measure(TextBox style, string text, double width, DpiScale dpi, int lines)
    {
        try
        {
            return FormatLines(style, text, width, dpi, lines);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            // Preview failures never hide the source body or stop the window.
            // Keep the actual native full-text rendering, not a guessed excerpt.
            System.Diagnostics.Trace.TraceWarning("History preview unavailable: {0}", error.GetType().Name);
            return new(double.PositiveInfinity, []);
        }
    }

    private static HistoryBodyMeasurement FormatLines(TextBox style, string text, double width, DpiScale dpi, int lines)
    {
        var available = Math.Max(1, width - 2 * NativeCaretGutter - style.Padding.Left - style.Padding.Right -
            style.BorderThickness.Left - style.BorderThickness.Right);
        var run = new RunStyle(style) { PixelsPerDip = dpi.PixelsPerDip };
        var source = new HistoryTextSource(text, run) { PixelsPerDip = dpi.PixelsPerDip };
        var paragraph = new ParagraphStyle(style, run);
        var excerpts = new List<HistoryExcerpt>(lines);
        using var formatter = TextFormatter.Create(TextOptions.GetTextFormattingMode(style));
        TextLineBreak? continuation = null;
        var index = 0;
        var height = 0.0;
        var count = 0;
        double RoundHeight(double value) => Math.Ceiling(value * dpi.DpiScaleY) / dpi.DpiScaleY;
        try
        {
            for (;;)
            {
                using var line = formatter.FormatLine(source, index, available, paragraph, continuation);
                continuation?.Dispose();
                continuation = null;
                if (line.Length <= 0) throw new InvalidOperationException("History line formatting did not advance.");
                var next = index + line.Length;
                var end = Math.Min(next, text.Length);
                height += line.Height;
                count++;
                if (count <= lines)
                {
                    var visibleEnd = end;
                    if (visibleEnd > index && text[visibleEnd - 1] == '\n') visibleEnd--;
                    if (visibleEnd > index && text[visibleEnd - 1] == '\r') visibleEnd--;
                    excerpts.Add(new(text[..visibleEnd], RoundHeight(height)));
                }
                // A real final newline has a visible empty line after it.
                // A synthetic end-of-paragraph marker lies beyond the text.
                if (next > text.Length || next == text.Length &&
                    (end == 0 || text[end - 1] is not ('\r' or '\n')))
                    break;
                index = next;
                continuation = line.GetTextLineBreak();
            }
        }
        finally { continuation?.Dispose(); }
        return new(RoundHeight(height), count > lines ? excerpts.ToArray() : []);
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
                length == 0 ? CharacterBufferRange.Empty : new CharacterBufferRange(text, 0, length)));
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
