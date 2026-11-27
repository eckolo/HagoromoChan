using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
        probe.Arrange(new Rect(0, 0, width, full));
        if (probe.LineCount <= lines) return new(full, []);

        // Obtain wrap boundaries from the same WPF TextBox template and font
        // as the live card. The text that is stored/copied is never changed.
        var excerpts = new List<HistoryExcerpt>(lines);
        var boundaries = Enumerable.Range(1, lines).Select(probe.GetCharacterIndexFromLineIndex).ToArray();
        foreach (var end in boundaries)
        {
            if (end <= 0 || end > text.Length)
                return new(full, []);
            var excerpt = text[..end];
            if (excerpt.EndsWith("\r\n", StringComparison.Ordinal))
                excerpt = excerpt[..^2];
            else if (excerpt.EndsWith('\n') || excerpt.EndsWith('\r'))
                excerpt = excerpt[..^1];
            probe.Text = excerpt;
            probe.Measure(space);
            excerpts.Add(new(excerpt, probe.DesiredSize.Height));
        }
        return new(full, excerpts.ToArray());
    }
}

internal sealed record HistoryExcerpt(string Text, double Height);
