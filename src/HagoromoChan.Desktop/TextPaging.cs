using System.Globalization;

namespace HagoromoChan.Desktop;

internal readonly record struct TextSlice(int Start, int Length, double Height)
{
    public int End => Start + Length;
    public string Read(string text) => text.Substring(Start, Length);
}

internal static class TextPaging
{
    // Preserve every original UTF-16 code unit, including CRLF and combining characters.
    // The caller supplies the measurement of the actual text control at its final width.
    public static IReadOnlyList<TextSlice> Split(string text, double maximumHeight, Func<string, double> measure)
    {
        if (text.Length == 0)
            return [new TextSlice(0, 0, measure(""))];

        var starts = StringInfo.ParseCombiningCharacters(text);
        var pages = new List<TextSlice>();
        int Boundary(int index) => index == starts.Length ? text.Length : starts[index];

        for (var start = 0; start < starts.Length;)
        {
            double Height(int end) => measure(text.Substring(Boundary(start), Boundary(end) - Boundary(start)));
            var firstHeight = Height(start + 1);
            if (firstHeight > maximumHeight)
                throw new InvalidOperationException("The available display area cannot fit one text element.");

            var low = start + 1;
            var high = low;
            // Do not repeatedly measure the entire remaining long message.
            var stride = 1;
            while (high < starts.Length)
            {
                var candidate = Math.Min(starts.Length, high + stride);
                if (Height(candidate) > maximumHeight)
                {
                    high = candidate;
                    break;
                }
                low = high = candidate;
                stride = (int)Math.Min(starts.Length, (long)stride * 2);
            }
            while (low + 1 < high)
            {
                var middle = low + (high - low) / 2;
                if (Height(middle) <= maximumHeight) low = middle;
                else high = middle;
            }
            var height = Height(low);
            pages.Add(new TextSlice(Boundary(start), Boundary(low) - Boundary(start), height));
            start = low;
        }

        return pages;
    }

    public static int PageAt(IReadOnlyList<TextSlice> pages, int offset)
    {
        for (var i = pages.Count - 1; i >= 0; i--)
            if (pages[i].Start <= offset) return i;
        return 0;
    }

    public static string Replace(string text, TextSlice slice, string replacement) =>
        text[..slice.Start] + replacement + text[slice.End..];
}

internal readonly record struct HistoryPosition(int Message, int Page);

internal static class HistoryNavigation
{
    public static HistoryPosition? Older(HistoryPosition current, Func<int, int> pageCount) =>
        current.Page > 0 ? current with { Page = current.Page - 1 } :
        current.Message > 0 ? new HistoryPosition(current.Message - 1, pageCount(current.Message - 1) - 1) :
        null;

    public static HistoryPosition? Newer(HistoryPosition current, int messageCount, Func<int, int> pageCount) =>
        current.Page + 1 < pageCount(current.Message) ? current with { Page = current.Page + 1 } :
        current.Message + 1 < messageCount ? new HistoryPosition(current.Message + 1, 0) :
        null;

    // Expand toward the latest message until the available area is filled. Advancing the
    // starting position removes the oldest card; at capacity it reveals the next card.
    public static IReadOnlyList<HistoryPosition> Fit(HistoryPosition first, int count,
        Func<int, int> pageCount, Func<HistoryPosition, double> cardHeight, double available)
    {
        var result = new List<HistoryPosition>();
        var height = 0.0;
        for (HistoryPosition? next = first; next is { } position; next = Newer(position, count, pageCount))
        {
            var needed = cardHeight(position);
            if (height + needed > available + .01) break;
            result.Add(position);
            height += needed;
        }
        return result;
    }
}

internal readonly record struct ConversationVisibility(bool Input, bool History)
{
    public bool Any => Input || History;
    public ConversationVisibility ToggleInput() => this with { Input = !Input };
    public ConversationVisibility ToggleHistory() => this with { History = !History };
    public ConversationVisibility ToggleAll() => Any ? default : new(true, true);
}

internal readonly record struct SequenceEdit<T>(bool Remove, int Index, T Item);

// Equal retained keys stay in the existing collection rather than being recreated.
// The inputs consist of unique keys; ordinary history changes preserve relative order.
internal static class RetainedSequence
{
    public static IReadOnlyList<SequenceEdit<T>> Plan<T>(IReadOnlyList<T> current,
        IReadOnlyList<T> desired) where T : notnull
    {
        var state = current.ToList();
        var wanted = desired.ToHashSet();
        var edits = new List<SequenceEdit<T>>();
        for (var i = state.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(state[i])) continue;
            edits.Add(new(true, i, state[i]));
            state.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < state.Count && EqualityComparer<T>.Default.Equals(state[i], item))
                continue;
            var existing = state.IndexOf(item);
            if (existing >= 0)
            {
                edits.Add(new(true, existing, item));
                state.RemoveAt(existing);
            }
            edits.Add(new(false, i, item));
            state.Insert(i, item);
        }
        return edits;
    }
}
