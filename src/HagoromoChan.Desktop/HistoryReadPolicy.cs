namespace HagoromoChan.Desktop;

internal enum HistoryReadGesture { Passive, Activation, Navigation }

internal readonly record struct HistoryPageIdentity(string Id, int Start, int Length);

internal static class HistoryReadPolicy
{
    internal static string[] Targets(HistoryReadGesture gesture, bool active, bool composerFocused,
        IReadOnlyList<HistoryPageIdentity> before, IReadOnlyList<HistoryPageIdentity> offered,
        IReadOnlySet<HistoryPageIdentity> laidOut, IReadOnlySet<string> unread)
    {
        if (gesture == HistoryReadGesture.Passive
            || (gesture == HistoryReadGesture.Activation && (!active || composerFocused))
            || (gesture == HistoryReadGesture.Navigation && before.SequenceEqual(offered)))
            return [];

        // Newness belongs to the answer, not each page. An answer is read once if
        // at least one of its actual displayed pages was offered by this gesture.
        return offered.Where(page => laidOut.Contains(page) && unread.Contains(page.Id))
            .Select(page => page.Id).Distinct(StringComparer.Ordinal).ToArray();
    }
}
