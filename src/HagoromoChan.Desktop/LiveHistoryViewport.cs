using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HagoromoChan.Desktop;

// Acts solely as a viewport: the actual read-only TextBox keeps one complete
// layout, selection, and document. Page changes move the visual, never its Text.
internal sealed class LiveHistoryViewport(TextBox body) : Decorator
{
    internal readonly record struct Page(TextSlice Slice, double Top);
    private IReadOnlyList<Page> _pages = [];
    private double _fullHeight;
    private double _width = -1;
    private double _scale;
    private double _maximum;
    private bool _geometryKnown;
    private bool _loadRetryUsed;
    private bool _forceFallback;

    public event Action? GeometryInvalidated;
    private string? _measured;
    private Page _current;

    public TextBox Body { get; } = body;
    public bool GeometryKnown => _geometryKnown;
    public double ViewTop => _current.Top;

    public IReadOnlyList<Page> Layout(double width, double maximum, double scale)
    {
        if (!ReferenceEquals(Child, Body))
        {
            Child = Body;
            ClipToBounds = true;
        }
        if (_measured == Body.Text && width == _width && maximum == _maximum && scale == _scale)
            return _pages;
        if (width != _width || maximum != _maximum || scale != _scale)
        {
            _loadRetryUsed = false;
            _forceFallback = false;
        }
        _measured = Body.Text;
        _width = Width = width;
        _maximum = maximum;
        _scale = scale;
        Body.Width = Body.MaxWidth = width;
        Body.ClipToBounds = false;
        Body.ApplyTemplate();
        Body.Measure(new Size(width, double.PositiveInfinity));
        _fullHeight = BubbleLayout.RoundUp(Body.DesiredSize.Height, scale);
        Body.Arrange(new Rect(0, 0, width, _fullHeight));
        var drawn = ReadLines(Body);
        if (drawn.Count != 0)
        {
            var lastInk = drawn.Where(line => !line.Ink.IsEmpty).Select(line => line.Ink.Bottom)
                .DefaultIfEmpty(0).Max();
            if (lastInk > _fullHeight)
            {
                _fullHeight = BubbleLayout.RoundUp(lastInk, scale);
                Body.Arrange(new Rect(0, 0, width, _fullHeight));
                drawn = ReadLines(Body);
            }
        }
        var complete = _forceFallback ? null : TryPages(drawn, Body, _fullHeight, maximum, scale);
        _geometryKnown = complete is not null;
        _pages = complete ?? [new(new(0, Body.Text.Length, Math.Min(maximum, Math.Max(44, _fullHeight))), 0)];
        return _pages;
    }

    public bool BeginOneGeometryRetry()
    {
        if (_geometryKnown || _forceFallback || _loadRetryUsed) return false;
        _loadRetryUsed = true;
        return true;
    }

    public void InvalidateUnverifiedLayout() => _measured = null;

    public void Select(Page page)
    {
        _current = page;
        Height = page.Slice.Height;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Body.Measure(new Size(_width > 0 ? _width : availableSize.Width,
            _geometryKnown ? double.PositiveInfinity : _current.Slice.Height));
        return new Size(Body.DesiredSize.Width, _current.Slice.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Body.Arrange(new Rect(0, -_current.Top, Math.Max(0, _width),
            _geometryKnown ? _fullHeight : arrangeSize.Height));
        // Inspect the final arrangement WPF gave the actual native child,
        // not merely the earlier full-height preparation pass.
        if (_geometryKnown && (arrangeSize.Height + 1e-6 < _current.Slice.Height ||
            Body.ActualHeight + 1e-6 < _fullHeight || Body.ActualWidth + 1e-6 < _width))
        {
            _geometryKnown = false;
            _forceFallback = true;
            _measured = null;
            GeometryInvalidated?.Invoke();
        }
        return arrangeSize;
    }

    private readonly record struct DrawnLine(double Origin, Rect Ink);

    private static List<DrawnLine> ReadLines(TextBox body)
    {
        var lines = new List<DrawnLine>();
        void Collect(DependencyObject element)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                // TextBoxView exposes its rendered lines as DrawingVisuals.
                // Confirm their one-to-one line correspondence below; don't
                // depend on a private type name, template automation or probe.
                if (child is DrawingVisual visual)
                {
                    var transform = visual.TransformToAncestor(body);
                    var origin = transform.Transform(new Point(0, 0)).Y;
                    var bounds = VisualTreeHelper.GetDrawing(visual)?.Bounds ?? Rect.Empty;
                    lines.Add(new(origin, bounds.IsEmpty ? Rect.Empty : transform.TransformBounds(bounds)));
                }
                else Collect(child);
            }
        }
        Collect(body);
        lines.Sort((a, b) => a.Origin.CompareTo(b.Origin));
        return lines;
    }

    private static IReadOnlyList<Page>? TryPages(IReadOnlyList<DrawnLine> lines, TextBox body,
        double fullHeight, double maximum, double scale)
    {
        if (maximum <= 0 || lines.Count != body.LineCount || lines.Count == 0)
            return null;
        var starts = new int[lines.Count];
        // Validate the geometry actually comes from the textbox line layout,
        // not from unrelated adorners or an incomplete virtualized viewport.
        double? firstCaret = null;
        for (var i = 0; i < lines.Count; i++)
        {
            starts[i] = body.GetCharacterIndexFromLineIndex(i);
            if (starts[i] < 0 || starts[i] > body.Text.Length || (i > 0 && starts[i] < starts[i - 1]))
                return null;
            if (starts[i] == body.Text.Length && i > 0) continue; // terminal empty line has no glyph
            var rect = body.GetRectFromCharacterIndex(starts[i]);
            if (rect.IsEmpty) return null;
            firstCaret ??= rect.Top;
            if (Math.Abs((rect.Top - firstCaret.Value) - (lines[i].Origin - lines[0].Origin)) > 1 / scale)
                return null;
        }

        var topSuffix = new double[lines.Count + 1];
        topSuffix[^1] = double.PositiveInfinity;
        for (var i = lines.Count - 1; i >= 0; i--)
            topSuffix[i] = Math.Min(topSuffix[i + 1], lines[i].Ink.IsEmpty ? double.PositiveInfinity : lines[i].Ink.Top);
        var cuts = new List<(int Offset, double Top)> { (0, 0) };
        var precedingBottom = 0.0;
        if (topSuffix[0] < 0) return null;
        for (var i = 1; i < lines.Count; i++)
        {
            if (!lines[i - 1].Ink.IsEmpty)
                precedingBottom = Math.Max(precedingBottom, lines[i - 1].Ink.Bottom);
            // A cut between overlapping drawn lines would clip at least one
            // line. Keep such neighboring lines on the same page instead.
            var lo = BubbleLayout.RoundUp(precedingBottom, scale);
            var hi = BubbleLayout.RoundDown(topSuffix[i], scale);
            if (lo > hi || starts[i] <= cuts[^1].Offset || starts[i] >= body.Text.Length) continue;
            var cut = Math.Clamp(BubbleLayout.RoundDown(lines[i].Origin, scale), lo, hi);
            if (cut > cuts[^1].Top && cut < fullHeight) cuts.Add((starts[i], cut));
        }
        if (!lines[^1].Ink.IsEmpty) precedingBottom = Math.Max(precedingBottom, lines[^1].Ink.Bottom);
        if (precedingBottom > fullHeight) return null;
        cuts.Add((body.Text.Length, fullHeight));
        if (fullHeight <= maximum)
            return [new(new(0, body.Text.Length, fullHeight), 0)];

        var pages = new List<Page>();
        for (var i = 0; i < cuts.Count - 1;)
        {
            var end = i + 1;
            while (end < cuts.Count && cuts[end].Top - cuts[i].Top <= maximum) end++;
            end--;
            if (end == i) return null;
            pages.Add(new(new(cuts[i].Offset, cuts[end].Offset - cuts[i].Offset, cuts[end].Top - cuts[i].Top),
                cuts[i].Top));
            i = end;
        }
        return pages;
    }
}
