using HagoromoChan.LayoutChecks;

internal static class TerminalMaskChecks
{
    internal static void Run()
    {
        // Isolated top mark, gap inside the glyph, body, one-pixel descender.
        byte[] marker = [0,0,0, 0,1,0, 0,0,0, 1,1,1, 0,0,1, 0,0,0];
        Check(marker, TerminalMaskResult.Match, 4);
        var clipped = (byte[])marker.Clone();
        Array.Clear(clipped, 4 * 3, 3);
        Check(clipped, TerminalMaskResult.BottomRowsMissing, 3);
        clipped[3 * 3] = 0;
        Check(clipped, TerminalMaskResult.OtherMismatch, 3);
        var shifted = new byte[marker.Length];
        marker.AsSpan(3).CopyTo(shifted);
        Check(shifted, TerminalMaskResult.OtherMismatch, 3);
        Check(new byte[marker.Length], TerminalMaskResult.OtherMismatch, -1);
        if (TerminalMask.Compare(new byte[3], new byte[3], 3, out _, out _) != TerminalMaskResult.EmptyReference)
            throw new InvalidOperationException("A blank reference cannot pass.");

        void Check(byte[] observed, TerminalMaskResult expected, int tail)
        {
            var actual = TerminalMask.Compare(marker, observed, 3, out var referenceTail, out var shownTail);
            if (actual != expected || referenceTail != 4 || shownTail != tail)
                throw new InvalidOperationException($"Terminal mask expected {expected}/4/{tail}, got {actual}/{referenceTail}/{shownTail}");
        }
    }
}
