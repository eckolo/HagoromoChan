namespace HagoromoChan.Relay;

public sealed record WorkWakeRegistration(Subscription Subscription, string? PreviousSecret, DateTimeOffset? RotateUntil)
{
    public string? PreviousSecretAt(DateTimeOffset now) => RotateUntil > now ? PreviousSecret : null;
}

public sealed partial class ChatStore
{
    private void InitializeWorkWake()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            -- Preserve the pilot store names for in-place upgrades and rollback.
            CREATE TABLE IF NOT EXISTS m05_work_wake_subscription (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                id TEXT NOT NULL, callback_url TEXT NOT NULL, secret TEXT NOT NULL,
                refresh_before TEXT NOT NULL, previous_secret TEXT, rotate_until TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    public WorkWakeRegistration? GetWorkWakeRegistration()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            return ReadWorkWakeRegistration();
        }
    }

    public void SaveWorkWakeSubscription(Subscription subscription, DateTimeOffset now)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var previous = ReadWorkWakeRegistration();
            if (previous is not null && previous.Subscription.RefreshBefore > now &&
                previous.Subscription.Id != subscription.Id)
                throw new ConflictException("The Work wake subscription has an active owner.");
            var same = previous is not null && previous.Subscription.Id == subscription.Id &&
                previous.Subscription.RefreshBefore > now;
            string? previousSecret = null;
            DateTimeOffset? rotateUntil = null;
            if (same && previous!.Subscription.Secret != subscription.Secret)
            {
                previousSecret = previous.Subscription.Secret;
                rotateUntil = now.AddMinutes(5);
            }
            else if (same && previous!.PreviousSecretAt(now) is { } activeSecret)
            {
                previousSecret = activeSecret;
                rotateUntil = previous.RotateUntil;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO m05_work_wake_subscription
                    (singleton, id, callback_url, secret, refresh_before, previous_secret, rotate_until)
                VALUES (1, $id, $callback, $secret, $refresh, $previous, $rotation)
                ON CONFLICT(singleton) DO UPDATE SET id=excluded.id, callback_url=excluded.callback_url,
                    secret=excluded.secret, refresh_before=excluded.refresh_before,
                    previous_secret=excluded.previous_secret, rotate_until=excluded.rotate_until;
                """;
            command.Parameters.AddWithValue("$id", subscription.Id);
            command.Parameters.AddWithValue("$callback", subscription.CallbackUrl);
            command.Parameters.AddWithValue("$secret", subscription.Secret);
            command.Parameters.AddWithValue("$refresh", Timestamp(subscription.RefreshBefore));
            command.Parameters.AddWithValue("$previous", (object?)previousSecret ?? DBNull.Value);
            command.Parameters.AddWithValue("$rotation", rotateUntil is { } until ? Timestamp(until) : (object)DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveWorkWakeSubscription(string id)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM m05_work_wake_subscription WHERE id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private WorkWakeRegistration? ReadWorkWakeRegistration()
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, callback_url, secret, refresh_before, previous_secret, rotate_until
            FROM m05_work_wake_subscription WHERE singleton = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTimestamp(reader.GetString(3))),
            reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : ParseTimestamp(reader.GetString(5)));
    }

}
