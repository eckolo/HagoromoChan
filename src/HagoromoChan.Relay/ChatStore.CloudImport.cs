using System.Globalization;
using HagoromoChan.Contracts;
using Microsoft.Data.Sqlite;

namespace HagoromoChan.Relay;

public sealed partial class ChatStore
{
    private const int MaximumCloudSources = 250;
    private const string WorkProviderPrefix = "chatgpt-work:";

    // Additive tables only: the Dot schema version, messages, cursor and subscriptions stay intact.
    private void InitializeCloudImports()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            -- Preserve the pilot store names for in-place upgrades and rollback.
            CREATE TABLE IF NOT EXISTS m05_sources (
                id TEXT PRIMARY KEY NOT NULL, kind TEXT NOT NULL, url TEXT NOT NULL,
                title TEXT NOT NULL, conversation_id TEXT NOT NULL, UNIQUE(kind, conversation_id)
            );
            CREATE TABLE IF NOT EXISTS m05_answers (
                sequence INTEGER PRIMARY KEY, import_id TEXT NOT NULL UNIQUE,
                source_id TEXT NOT NULL REFERENCES m05_sources(id), body TEXT NOT NULL,
                received_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS m05_answers_source ON m05_answers(source_id, sequence);
            """;
        command.ExecuteNonQuery();
    }

    public CloudSource SaveCloudSource(SaveCloudSourceRequest request)
    {
        var title = request.Title?.Trim();
        if (title is null || title.Length is < 1 or > 200 || title.Any(char.IsControl))
            throw new ValidationException("会話名は1〜200文字で入力してください。");
        if (request.Kind is not ("chat" or "work"))
            throw new ValidationException("会話種別にChatまたはWorkを指定してください。");
        var (url, conversationId) = ValidateCloudUrl(request.Url);
        return SaveCloudSourceCore(request.Kind, url, title, conversationId);
    }

    internal CloudSource SaveDiscoveredWork(WorkSourceCandidate candidate) =>
        SaveCloudSourceCore("work", "", candidate.Title, WorkProviderPrefix + candidate.ConversationId);

    internal string WorkConversationId(string sourceId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind, conversation_id FROM m05_sources WHERE id = $id";
            command.Parameters.AddWithValue("$id", sourceId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException();
            if (reader.GetString(0) != "work")
                throw new ValidationException("Only registered Work conversations can be retrieved.");
            var persisted = reader.GetString(1);
            return persisted.StartsWith(WorkProviderPrefix, StringComparison.Ordinal)
                ? persisted[WorkProviderPrefix.Length..] : persisted;
        }
    }

    private CloudSource SaveCloudSourceCore(string kind, string url, string title, string conversationId)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM m05_sources WHERE kind = $kind AND conversation_id = $conversation";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$conversation", conversationId);
            var existing = command.ExecuteScalar() as string;
            command.Parameters.Clear();
            if (existing is null)
            {
                command.CommandText = "SELECT COUNT(*) FROM m05_sources";
                if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= MaximumCloudSources)
                    throw new ValidationException("登録できる会話の上限に達しました。");
                command.CommandText = "INSERT INTO m05_sources(id, kind, url, title, conversation_id) VALUES ($id, $kind, $url, $title, $conversation)";
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$conversation", conversationId);
            }
            else
            {
                command.CommandText = "UPDATE m05_sources SET url = $url, title = $title WHERE id = $id";
            }
            var id = existing ?? Guid.NewGuid().ToString("D");
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$title", title);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM m05_answers WHERE source_id = $id";
            command.Parameters.AddWithValue("$id", id);
            var latest = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return new CloudSource(id, kind, url, title, latest);
        }
    }

    public CloudSource[] ListCloudSources()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, kind, url, title,
                    COALESCE((SELECT MAX(sequence) FROM m05_answers WHERE source_id = m05_sources.id), 0)
                FROM m05_sources ORDER BY title COLLATE NOCASE, id;
                """;
            using var reader = command.ExecuteReader();
            var items = new List<CloudSource>();
            while (reader.Read())
                items.Add(new CloudSource(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
            return items.ToArray();
        }
    }

    public CloudAnswer ImportCloudAnswer(string sourceId, string importId, string text)
    {
        sourceId = ValidateRequestId(sourceId);
        importId = ValidateRequestId(importId);
        ValidateText(text);
        lock (gate)
        {
            ThrowIfDisposed();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT sequence, source_id, import_id, body, received_at FROM m05_answers WHERE import_id = $import";
            command.Parameters.AddWithValue("$import", importId);
            CloudAnswer? existing;
            using (var reader = command.ExecuteReader())
                existing = reader.Read() ? ReadCloudAnswer(reader) : null;
            if (existing is not null)
            {
                if (existing.SourceId != sourceId || existing.Text != text)
                    throw new ConflictException("同じ転送IDに異なる会話または本文が指定されています。");
                transaction.Commit();
                return existing;
            }
            command.CommandText = """
                INSERT INTO m05_answers(import_id, source_id, body, received_at)
                SELECT $import, id, $text, $now FROM m05_sources WHERE id = $source
                RETURNING sequence;
                """;
            var now = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            if (command.ExecuteScalar() is not long sequence)
                throw new KeyNotFoundException();
            transaction.Commit();
            return new CloudAnswer(sequence, sourceId, importId, text, now);
        }
    }

    public CloudAnswerPage ReadCloudAnswers(string sourceId, long after = 0, int limit = 50)
    {
        sourceId = ValidateRequestId(sourceId);
        if (after < 0) throw new ValidationException("位置には0以上の値を指定してください。");
        ValidateLimit(limit);
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM m05_sources WHERE id = $source";
            command.Parameters.AddWithValue("$source", sourceId);
            if (command.ExecuteScalar() is null) throw new KeyNotFoundException();
            command.CommandText = """
                SELECT sequence, source_id, import_id, body, received_at FROM m05_answers
                WHERE source_id = $source AND sequence > $after ORDER BY sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", limit + 1);
            using var reader = command.ExecuteReader();
            var items = new List<CloudAnswer>();
            while (reader.Read()) items.Add(ReadCloudAnswer(reader));
            var more = items.Count > limit;
            if (more) items.RemoveAt(limit);
            return new CloudAnswerPage(items.ToArray(), items.Count == 0 ? after : items[^1].Sequence, more);
        }
    }

    private static CloudAnswer ReadCloudAnswer(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture));

    private static (string Url, string ConversationId) ValidateCloudUrl(string? raw)
    {
        if (raw is null || raw.Length > 2048 ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.IdnHost != "chatgpt.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new ValidationException("ChatGPTの通常の会話URLを指定してください。");
        var parts = uri.AbsolutePath.TrimEnd('/').Split('/');
        var validPath = (parts.Length == 3 && parts[1] == "c") ||
            (parts.Length == 5 && parts[1] == "g" && parts[2].StartsWith("g-p-", StringComparison.Ordinal) && parts[3] == "c");
        if (!validPath || !Guid.TryParseExact(parts[^1], "D", out var id) || id == Guid.Empty)
            throw new ValidationException("共有リンクではなくChatGPTの会話URLを指定してください。");
        return ("https://chatgpt.com" + uri.AbsolutePath.TrimEnd('/'), id.ToString("D"));
    }
}
