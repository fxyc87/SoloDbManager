using Microsoft.Data.Sqlite;

namespace SoloDbManager;

/// <summary>
/// Persistent store for saved database connections, backed by a small SQLite
/// file (no Prisma / no heavy engine — keeps the AOT binary small).
/// </summary>
public sealed class ConnectionsStore
{
    private static SqliteConnection? _conn;
    private static readonly object _lock = new();

    private static string StorePath()
    {
        var envUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrEmpty(envUrl) && envUrl.StartsWith("file:"))
        {
            var p = envUrl["file:".Length..];
            try { Directory.CreateDirectory(Path.GetDirectoryName(p)!); return p; } catch { }
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".solodb-manager", "db");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, "connections.db");
    }

    private static SqliteConnection GetConn()
    {
        lock (_lock)
        {
            if (_conn != null)
            {
                try
                {
                    using var c = _conn.CreateCommand();
                    c.CommandText = "SELECT 1";
                    c.ExecuteScalar();
                    return _conn;
                }
                catch { _conn = null; }
            }
            var p = StorePath();
            var conn = new SqliteConnection($"Data Source={p}");
            conn.Open();
            using (var c = conn.CreateCommand())
            {
                c.CommandText = """
                    CREATE TABLE IF NOT EXISTS connection (
                      id TEXT PRIMARY KEY,
                      name TEXT NOT NULL,
                      path TEXT NOT NULL UNIQUE,
                      isSolodb INTEGER NOT NULL DEFAULT 0,
                      lastOpenedAt TEXT NOT NULL,
                      createdAt TEXT NOT NULL
                    );
                    """;
                c.ExecuteNonQuery();
            }
            _conn = conn;
            return conn;
        }
    }

    public static List<StoredConnection> List()
    {
        var result = new List<StoredConnection>();
        var conn = GetConn();
        lock (_lock)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT id, name, path, isSolodb, lastOpenedAt, createdAt FROM connection ORDER BY lastOpenedAt DESC";
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                result.Add(new StoredConnection
                {
                    Id = r.GetString("id"),
                    Name = r.GetString("name"),
                    Path = r.GetString("path"),
                    IsSolodb = r.GetInt32("isSolodb"),
                    LastOpenedAt = r.GetString("lastOpenedAt"),
                    CreatedAt = r.GetString("createdAt"),
                });
            }
        }
        return result;
    }

    public static StoredConnection Upsert(string path, string name, bool isSolodb)
    {
        var conn = GetConn();
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToString("o");
            StoredConnection? existing = null;
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "SELECT id, name, path, isSolodb, lastOpenedAt, createdAt FROM connection WHERE path = @p";
                c.Parameters.AddWithValue("@p", path);
                using var r = c.ExecuteReader();
                if (r.Read())
                {
                    existing = new StoredConnection
                    {
                        Id = r.GetString("id"),
                        Name = r.GetString("name"),
                        Path = r.GetString("path"),
                        IsSolodb = r.GetInt32("isSolodb"),
                        LastOpenedAt = r.GetString("lastOpenedAt"),
                        CreatedAt = r.GetString("createdAt"),
                    };
                }
            }
            if (existing != null)
            {
                using var c = conn.CreateCommand();
                c.CommandText = "UPDATE connection SET name = @n, isSolodb = @i, lastOpenedAt = @t WHERE id = @id";
                c.Parameters.AddWithValue("@n", name);
                c.Parameters.AddWithValue("@i", isSolodb ? 1 : 0);
                c.Parameters.AddWithValue("@t", now);
                c.Parameters.AddWithValue("@id", existing.Id);
                c.ExecuteNonQuery();
                existing.Name = name;
                existing.IsSolodb = isSolodb ? 1 : 0;
                existing.LastOpenedAt = now;
                return existing;
            }
            var id = Guid.NewGuid().ToString("N");
            using (var c = conn.CreateCommand())
            {
                c.CommandText = "INSERT INTO connection (id, name, path, isSolodb, lastOpenedAt, createdAt) VALUES (@id, @n, @p, @i, @t, @c)";
                c.Parameters.AddWithValue("@id", id);
                c.Parameters.AddWithValue("@n", name);
                c.Parameters.AddWithValue("@p", path);
                c.Parameters.AddWithValue("@i", isSolodb ? 1 : 0);
                c.Parameters.AddWithValue("@t", now);
                c.Parameters.AddWithValue("@c", now);
                c.ExecuteNonQuery();
            }
            return new StoredConnection { Id = id, Name = name, Path = path, IsSolodb = isSolodb ? 1 : 0, LastOpenedAt = now, CreatedAt = now };
        }
    }

    public static void Remove(string id)
    {
        var conn = GetConn();
        lock (_lock)
        {
            using var c = conn.CreateCommand();
            c.CommandText = "DELETE FROM connection WHERE id = @id";
            c.Parameters.AddWithValue("@id", id);
            c.ExecuteNonQuery();
        }
    }
}
