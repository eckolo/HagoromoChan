using HagoromoChan.Desktop;

internal static class HistoryPresentationChecks
{
    public static void RecentRange()
    {
        var policy = new HistoryPresentation();
        string[] history = ["new", "next", "third", "fourth"];
        var empty = new HashSet<string>(StringComparer.Ordinal);
        double Height(string _, bool preview) => preview ? 30 : 100;
        void Update(IReadOnlySet<string> visible) => policy.Recalculate(history, Height, 235, visible);
        void Require(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
        }
        Update(empty);
        Require(!policy.IsCollapsed("new") && !policy.IsCollapsed("next") && policy.IsCollapsed("third"),
            "Only consecutive full-height cards fit in the initial protected range.");
        policy.Collapse("new");
        Update(empty);
        Require(policy.IsCollapsed("new") && !policy.IsCollapsed("third") && policy.IsCollapsed("fourth"),
            "A manual collapse already in the protected range admits the next full card only.");
        policy.Expand("new"); // restores its full budget
        Update(new HashSet<string>(["third"], StringComparer.Ordinal));
        Require(!policy.IsCollapsed("third"), "An on-screen card remains open when leaving protection.");
        policy.LeftDisplay(["third"]);
        Require(policy.IsCollapsed("third"), "An older card collapses once no longer displayed.");
        policy.Expand("third");
        Require(!policy.IsCollapsed("third") && policy.IsCollapsed("fourth"), "Manual expansion is local.");
        policy.LeftDisplay(["third", "new"]);
        Require(policy.IsCollapsed("third") && !policy.IsCollapsed("new"), "The protected card must not auto-collapse.");
        policy.Recalculate(history, Height, 45, empty);
        Require(!policy.IsCollapsed("new") && policy.IsCollapsed("next"), "A window that fits only part of the latest message protects that message.");
    }

    public static void PreviewPreferences()
    {
        var root = Path.Combine(Path.GetTempPath(), "history-preview-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DesktopPreferencesStore(root);
            var p = store.Load();
            if (p.HistoryPreviewLines != 3) throw new InvalidOperationException("The default and older files should use three preview lines.");
            p.HistoryPreviewLines = 5;
            store.Save(p);
            if (store.Load().HistoryPreviewLines != 5) throw new InvalidOperationException("The user's choice did not survive restart.");
            p.HistoryPreviewLines = 50;
            store.Save(p);
            if (store.Load().HistoryPreviewLines != 3) throw new InvalidOperationException("An invalid preview count was not normalized.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
