using HagoromoChan.Desktop;

internal static class CursorGazeTests
{
    public static void Proximity()
    {
        // Left-side, 150%-scaled display. Eye is at (-940, 375), right edge at -880.
        var bounds = new ScreenRect(-1000, 300, 120, 300);
        var gaze = new CursorGaze();
        (double X, double Y)? At(double x, long ms) => gaze.Sample(bounds, 1.5, x, 375, ms);

        Check(At(-736, 0) is null, "96 DIP outside the edge starts a dwell");
        Check(At(-736, 299) is null, "brief passes do not engage");
        Check(At(-735, 300) is null, "leaving the entry zone interrupts an unfinished dwell");
        Check(At(-736, 400) is null && At(-736, 700) is { X: 1, Y: 0 }, "full nearby dwell");
        Check(At(-700, 710) is { X: 1 }, "120-DIP hysteresis while engaged");
        Check(At(-699, 720) is null && At(-709, 1100) is null, "wider exit ring cannot re-arm tracking");
        Check(At(-940, 1200) is null && At(-940, 1500) is null, "no direction at the exact eye position");
        Check(At(-950, 1510) is { X: -1 } && At(-940, 1520) is { X: -1 },
            "sub-DIP eye jitter preserves the last direction");

        gaze.Reset();
        Check(gaze.Sample(bounds, 1.5, -751, 171, 2000) is null
            && gaze.Sample(bounds, 1.5, -751, 171, 2300) is null,
            "the far corner of a rectangular margin lies outside the 96-DIP radius");
        Check(gaze.Sample(bounds, 1.5, -790, 210, 2400) is null
            && gaze.Sample(bounds, 1.5, -790, 210, 2700) is { X: > 0, Y: < 0 },
            "a nearby corner yields the direction from the eyes");
        Check(gaze.Sample(bounds, 0, -790, 210, 2750) is null && At(-850, 2760) is null,
            "invalid scale clears a stale target");
    }

    public static void PanelActivity()
    {
        var gaze = new CursorGaze();
        var bounds = new ScreenRect(0, 100, 160, 240);
        (double X, double Y)? At(double x, double y, long ms) => gaze.Sample(bounds, 1, x, y, ms);

        Check(At(600, 280, 500) is null && At(600, 280, 990) is null, "passive distant cursor");
        gaze.NotePanelInteraction(1000);
        Check(At(600, 280, 1000) is { X: > 0, Y: > 0 }, "panel input immediately directs gaze beyond range");
        Check(At(-500, 100, 1550) is { X: < 0, Y: < 0 }, "direction follows current pointer during panel hold");
        gaze.NotePanelInteraction(1800);
        Check(At(600, 280, 2799) is not null && At(600, 280, 2800) is null,
            "repeated scrolling refreshes a one-second hold; inactivity releases it");

        gaze.NotePanelInteraction(4000);
        Check(At(170, 140, 4000) is not null && At(170, 140, 4310) is not null
            && At(170, 140, 5000) is not null, "nearby dwell takes over naturally after panel hold");
        gaze.Reset();
        Check(At(170, 140, 5010) is null, "reset discards near dwell");
        gaze.NotePanelInteraction(5100);
        gaze.Reset();
        Check(At(650, 180, 5110) is null, "reset discards recent panel activity");
    }

    private static void Check(bool condition, string scenario)
    {
        if (!condition) throw new InvalidOperationException(scenario);
    }
}
