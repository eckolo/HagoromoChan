namespace HagoromoChan.LayoutChecks;

internal enum TerminalMaskResult { Match, BottomRowsMissing, OtherMismatch, EmptyReference }

internal static class TerminalMask
{
    // Input origin is set by the measured text anchor, never by the painted
    // bottom row. Registering to the last painted row would hide clipped ink.
    internal static TerminalMaskResult Compare(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> shown,
        int width, out int expectedLast, out int shownLast)
    {
        if (width <= 0 || expected.Length != shown.Length || expected.Length % width != 0)
            throw new ArgumentException("Both masks must cover the same integral pixel grid.");
        expectedLast = LastRow(expected, width);
        shownLast = LastRow(shown, width);
        if (expectedLast < 0) return TerminalMaskResult.EmptyReference;
        if (expected.SequenceEqual(shown)) return TerminalMaskResult.Match;
        if (shownLast >= 0 && shownLast < expectedLast &&
            expected[..((shownLast + 1) * width)].SequenceEqual(shown[..((shownLast + 1) * width)]))
            return TerminalMaskResult.BottomRowsMissing;
        return TerminalMaskResult.OtherMismatch;
    }

    private static int LastRow(ReadOnlySpan<byte> mask, int width)
    {
        for (var i = mask.Length - 1; i >= 0; i--)
            if (mask[i] != 0) return i / width;
        return -1;
    }
}
