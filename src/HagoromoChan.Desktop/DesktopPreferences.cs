using System.IO;
using System.Text.Json;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

internal sealed class DesktopPreferences
{
    // System.Text.Json can construct the internal type via its public constructor.
    public DesktopPreferences() { }

    public int? ScreenX { get; set; }
    public int? ScreenY { get; set; }
    public int Height { get; set; } = 260;
    public int LayoutVersion { get; set; }
    public bool Topmost { get; set; } = true;
    public bool ReactionsEnabled { get; set; } = true;
    public long? ObservedSequence { get; set; }
    public string? ObservedId { get; set; }
    public HashSet<string> UnreadIds { get; set; } = new(StringComparer.Ordinal);

    // Called only after a complete history read. A stored cursor must match the message
    // identity as well as the sequence, since a replacement database may reuse sequences.
    public bool Observe(IReadOnlyList<ChatMessage> messages, long cursor)
    {
        var confirmed = messages.Where(m => m.Sequence <= cursor).OrderBy(m => m.Sequence).ToArray();
        var last = confirmed.LastOrDefault();
        if (cursor != 0 && last?.Sequence != cursor)
            throw new InvalidDataException("通知基準のメッセージが見つかりません。");

        var validBaseline = ObservedSequence is long prior && prior >= 0 && prior <= cursor
            && (prior == 0 ? ObservedId is null : confirmed.Any(m => m.Sequence == prior && m.Id == ObservedId));
        var oldUnread = new HashSet<string>(UnreadIds, StringComparer.Ordinal);
        var knownAnswers = confirmed.Where(m => m.Role == "assistant").Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        UnreadIds.IntersectWith(knownAnswers);
        if (validBaseline)
        {
            foreach (var message in confirmed.Where(m => m.Role == "assistant" && m.Sequence > ObservedSequence!.Value))
                UnreadIds.Add(message.Id);
        }
        else
        {
            // First run or a replaced data store: the existing history is the baseline.
            // Do not present every historical answer as a newly arrived message.
            UnreadIds.Clear();
        }

        var changed = ObservedSequence != cursor || ObservedId != last?.Id || !oldUnread.SetEquals(UnreadIds);
        ObservedSequence = cursor;
        ObservedId = last?.Id;
        return changed;
    }
}

internal sealed class DesktopPreferencesStore(string directory)
{
    private readonly string _path = Path.Combine(directory, "desktop-ui.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DesktopPreferences Load()
    {
        if (!File.Exists(_path))
            return new DesktopPreferences { LayoutVersion = 2 };
        var value = JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(_path), Options)
            ?? throw new InvalidDataException("表示設定が空です。");
        // In v1, 180 was the standard size. In v2, 260 is the standard.
        if (value.LayoutVersion < 2 && value.Height == 180)
            value.Height = 260;
        value.Height = value.Height is 120 or 180 or 260 ? value.Height : 260;
        value.LayoutVersion = 2;
        value.UnreadIds ??= new HashSet<string>(StringComparer.Ordinal);
        if (value.ObservedSequence is < 0)
            throw new InvalidDataException("通知基準が不正です。");
        if (value.ScreenX is < -100000 or > 100000 || value.ScreenY is < -100000 or > 100000)
        {
            value.ScreenX = null;
            value.ScreenY = null;
        }
        return value;
    }

    public void Save(DesktopPreferences value)
    {
        var temporary = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
