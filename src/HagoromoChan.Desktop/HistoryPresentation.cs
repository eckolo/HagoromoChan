namespace HagoromoChan.Desktop;

// Protection uses one snapshot of the previous range, not the new results of
// auto-collapse. UI visibility and read receipts are deliberately separate.
internal sealed class HistoryPresentation
{
    public const int DefaultPreviewLines = 3;
    public const int MinimumPreviewLines = 2;
    public const int MaximumPreviewLines = 5;

    private HashSet<string> _protected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _manual = new(StringComparer.Ordinal);
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);

    public static int NormalizePreviewLines(int value) =>
        value is >= MinimumPreviewLines and <= MaximumPreviewLines ? value : DefaultPreviewLines;

    public int ProtectedCount => _protected.Count;

    public bool IsCollapsed(string id) => _manual.Contains(id) ||
        (!_protected.Contains(id) && !_open.Contains(id));

    public void Collapse(string id)
    {
        _open.Remove(id);
        // Outside the protected range, the default is already collapsed.
        if (_protected.Contains(id)) _manual.Add(id);
    }

    public void Expand(string id)
    {
        _manual.Remove(id);
        _open.Add(id);
    }

    public void Recalculate(IEnumerable<string> newestFirst,
        Func<string, bool, double> height, double budget, IReadOnlySet<string> displayed, double minimumCard = 0)
    {
        var previous = _protected;
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in newestFirst)
        {
            var size = height(id, previous.Contains(id) && _manual.Contains(id));
            if (next.Count > 0 && Math.Max(size, minimumCard) > budget) break;
            next.Add(id); // the latest always remains readable through native scroll
            budget -= size;
            if (budget <= 0) break;
        }

        foreach (var id in displayed)
            if (previous.Contains(id) && !next.Contains(id) && !_manual.Contains(id))
                _open.Add(id);

        _protected = next;
    }

    public void LeftDisplay(IEnumerable<string> ids)
    {
        // Protection already keeps recent cards open; a temporary opening
        // must not outlive display and later revive a now-older card.
        _open.ExceptWith(ids);
    }

    public void KeepOnly(IReadOnlySet<string> ids)
    {
        _protected.IntersectWith(ids);
        _manual.IntersectWith(ids);
        _open.IntersectWith(ids);
    }
}
