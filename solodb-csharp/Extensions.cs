using Microsoft.Data.Sqlite;

namespace SoloDbManager;

/// <summary>
/// Extension methods so reader calls can use column names instead of ordinals.
/// (Microsoft.Data.Sqlite's typed getters only accept int ordinals.)
/// </summary>
internal static class SqliteReaderExtensions
{
    public static string GetString(this SqliteDataReader r, string name) => r.GetString(r.GetOrdinal(name));
    public static int GetInt32(this SqliteDataReader r, string name) => r.GetInt32(r.GetOrdinal(name));
    public static long GetInt64(this SqliteDataReader r, string name) => r.GetInt64(r.GetOrdinal(name));
    public static bool IsDBNull(this SqliteDataReader r, string name) => r.IsDBNull(r.GetOrdinal(name));
}
