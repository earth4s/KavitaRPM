using System;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kavita.Services.Metadata;

// Access is serialized by ComicVineService. Reserve each request before sending it so a
// crash cannot restore a spent request. Cache entries never contain the authenticated URI.
internal sealed class ComicVineStore(string path) : IDisposable
{
    private SqliteConnection? _connection;
    private SqliteConnection Connection
    {
        get
        {
            if (_connection != null) return _connection;
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            try
            {
                db.Open();
                using var schema = db.CreateCommand();
                schema.CommandText = """
                    CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY, data TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS cache (key TEXT PRIMARY KEY, expires INTEGER NOT NULL, data TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS cache_expiry ON cache(expires);
                    """;
                schema.ExecuteNonQuery();
                return _connection = db;
            }
            catch { db.Dispose(); throw; }
        }
    }

    internal sealed record State(string Fingerprint, DateTimeOffset[] Requests, DateTimeOffset BlockedUntil,
        DateTimeOffset? LastSuccess, DateTimeOffset? LastFailure, string? Failure);

    internal State? ReadState()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT data FROM state WHERE id = 1";
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<State>(json) : null;
    }

    internal void WriteState(State state, bool clearCache = false)
    {
        using var transaction = Connection.BeginTransaction();
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = (clearCache ? "DELETE FROM cache;" : "") +
            "INSERT OR REPLACE INTO state(id,data) VALUES(1,$data)";
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(state));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal JsonElement? Read(string key, DateTimeOffset now)
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "SELECT data FROM cache WHERE key=$key AND expires>$now";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<JsonElement>(json) : null;
    }

    internal void Write(string key, JsonElement data, DateTimeOffset expires, int capacity)
    {
        using var transaction = Connection.BeginTransaction();
        using var command = Connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO cache(key,expires,data) VALUES($key,$expires,$data);
            DELETE FROM cache WHERE key IN (SELECT key FROM cache ORDER BY expires DESC LIMIT -1 OFFSET $capacity);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$expires", expires.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$data", data.GetRawText());
        command.Parameters.AddWithValue("$capacity", capacity);
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Dispose() => _connection?.Dispose();
}
