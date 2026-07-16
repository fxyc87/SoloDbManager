using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SoloDbManager;

/// <summary>
/// Core SoloDB / SQLite engine: detection, JSON flattening, CRUD, raw queries.
/// Mirrors the behaviour of the TypeScript `src/lib/solodb.ts` so the API
/// contract stays identical.
/// </summary>
public sealed class SoloDbEngine
{
    // ---- connection cache ----
    private static readonly Dictionary<string, SqliteConnection> _cache = new();
    private static readonly object _cacheLock = new();

    public static string ResolveDbPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Database path is required");
        var p = raw.Trim();
        if (p.StartsWith("~"))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p[1..]);
        if (!Path.IsPathRooted(p))
            p = Path.Combine(Directory.GetCurrentDirectory(), p);
        return Path.GetFullPath(p);
    }

    public static SqliteConnection GetDatabase(string rawPath, bool readOnly = false)
    {
        var abs = ResolveDbPath(rawPath);
        var key = abs + (readOnly ? ":ro" : ":rw");
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                try
                {
                    using var cmd = existing.CreateCommand();
                    cmd.CommandText = "SELECT 1";
                    cmd.ExecuteScalar();
                    return existing;
                }
                catch
                {
                    _cache.Remove(key);
                }
            }
            if (!File.Exists(abs) && readOnly)
                throw new FileNotFoundException($"Database file not found: {abs}");

            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = abs,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            }.ToString();
            var conn = new SqliteConnection(connStr);
            conn.Open();
            try { Exec(conn, "PRAGMA journal_mode = WAL"); } catch { }
            try { Exec(conn, "PRAGMA foreign_keys = ON"); } catch { }
            _cache[key] = conn;
            return conn;
        }
    }

    public static void CloseDatabase(string rawPath)
    {
        var abs = ResolveDbPath(rawPath);
        lock (_cacheLock)
        {
            var keys = _cache.Keys.Where(k => k.StartsWith(abs)).ToList();
            foreach (var k in keys)
            {
                try { _cache[k].Dispose(); } catch { }
                _cache.Remove(k);
            }
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ---- identifier quoting ----
    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // A column value that yields JSON text whether the stored column is binary
    // JSONB or plain-text JSON (or NULL).
    private static string JsonTextExpr(string column) =>
        $"CASE WHEN {QuoteIdent(column)} IS NULL THEN NULL ELSE json({QuoteIdent(column)}) END";

    // ---- table introspection ----
    public static List<ColumnInfo> GetTableColumns(SqliteConnection conn, string table)
    {
        var result = new List<ColumnInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({QuoteIdent(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new ColumnInfo
            {
                Name = r.GetString("name"),
                Type = r.IsDBNull("type") ? "" : r.GetString("type"),
                NotNull = r.GetInt32("notnull") == 1,
                DefaultValue = r.IsDBNull("dflt_value") ? null : r.GetString("dflt_value"),
                PrimaryKey = r.GetInt32("pk") > 0,
                Hidden = 0,
            });
        }
        return result;
    }

    public static List<IndexInfo> GetTableIndexes(SqliteConnection conn, string table)
    {
        var result = new List<IndexInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA index_list({QuoteIdent(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var idxName = r.GetString("name");
            var idx = new IndexInfo
            {
                Name = idxName,
                Unique = r.GetInt32("unique") == 1,
                Partial = r.GetInt32("partial") == 1,
                Sql = null,
            };
            using var c2 = conn.CreateCommand();
            c2.CommandText = $"PRAGMA index_info({QuoteIdent(idxName)})";
            using var r2 = c2.ExecuteReader();
            while (r2.Read())
            {
                idx.Columns.Add(r2.IsDBNull("name") ? "" : r2.GetString("name"));
            }
            result.Add(idx);
        }
        return result;
    }

    public static List<string> GetPkColumns(SqliteConnection conn, string table) =>
        GetTableColumns(conn, table).Where(c => c.PrimaryKey).Select(c => c.Name).ToList();

    public static TableKind DetectTableKind(SqliteConnection conn, string table)
    {
        if (table.StartsWith("SoloDB")) return TableKind.SolodbInternal;
        var cols = GetTableColumns(conn, table);
        var names = cols.Select(c => c.Name).ToList();
        bool hasId = names.Contains("Id");
        bool hasValue = names.Contains("Value");
        bool hasMetadata = names.Contains("Metadata");
        if (hasId && hasValue && hasMetadata && cols.Count <= 4)
        {
            var idCol = cols.FirstOrDefault(c => c.Name == "Id");
            if (idCol is { PrimaryKey: true }) return TableKind.SolodbCollection;
        }
        return TableKind.Regular;
    }

    public static long GetTableRowCount(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {QuoteIdent(table)}";
            return (long)cmd.ExecuteScalar()!;
        }
        catch { return 0; }
    }

    public static long? GetTableSizeBytes(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SUM(\"pgsize\") FROM dbstat WHERE name = @n";
            cmd.Parameters.AddWithValue("@n", table);
            var res = cmd.ExecuteScalar();
            return res == null || res == DBNull.Value ? null : (long)res;
        }
        catch { return null; }
    }

    public static List<TableSummary> ListTables(SqliteConnection conn)
    {
        var result = new List<TableSummary>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '_sqlite_%' ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            result.Add(new TableSummary
            {
                Name = name,
                Kind = DetectTableKind(conn, name),
                RowCount = GetTableRowCount(conn, name),
                SizeBytes = GetTableSizeBytes(conn, name),
            });
        }
        return result;
    }

    public static TableSchema GetTableSchema(SqliteConnection conn, string table)
    {
        var schema = new TableSchema
        {
            Name = table,
            Kind = DetectTableKind(conn, table),
            Columns = GetTableColumns(conn, table),
            Indexes = GetTableIndexes(conn, table),
            RowCount = GetTableRowCount(conn, table),
            SizeBytes = GetTableSizeBytes(conn, table),
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name = @n";
        cmd.Parameters.AddWithValue("@n", table);
        var res = cmd.ExecuteScalar();
        schema.CreateSql = res == null || res == DBNull.Value ? null : (string)res;

        if (schema.Kind == TableKind.SolodbCollection)
            schema.DocumentFields = GetDocumentFields(conn, table);

        return schema;
    }

    // Infer document field names/types by sampling up to N rows.
    public static List<DocumentField> GetDocumentFields(SqliteConnection conn, string table, int sampleSize = 200)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {JsonTextExpr("Value")} AS v FROM {QuoteIdent(table)} WHERE \"Value\" IS NOT NULL LIMIT {sampleSize}";
        var rows = new List<string?>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(r.IsDBNull("v") ? null : r.GetString("v"));

        var fieldMap = new Dictionary<string, FieldAcc>();

        foreach (var v in rows)
        {
            if (string.IsNullOrEmpty(v)) continue;
            using var doc = JsonDocument.Parse(v);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!fieldMap.TryGetValue(prop.Name, out var acc))
                {
                    acc = new FieldAcc();
                    fieldMap[prop.Name] = acc;
                }
                var (t, sample) = Classify(prop.Value);
                acc.Types.Add(t);
                if (prop.Value.ValueKind == JsonValueKind.Null) acc.Nullable = true;
                if (acc.Sample == null && sample != null) acc.Sample = sample;
            }
            // mark missing keys nullable
            foreach (var kv in fieldMap)
            {
                if (!doc.RootElement.TryGetProperty(kv.Key, out _))
                    kv.Value.Nullable = true;
            }
        }

        return fieldMap
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var types = kv.Value.Types.Where(t => t != "null").ToList();
                string jsType = types.Count == 0 ? "null" : types.Count == 1 ? types[0] : "mixed";
                return new DocumentField
                {
                    Key = kv.Key,
                    JsType = jsType,
                    Nullable = kv.Value.Nullable,
                    Sample = kv.Value.Sample,
                };
            })
            .ToList();
    }

    private static (string type, JsonNode? sample) Classify(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => ("null", null),
            JsonValueKind.True => ("boolean", true),
            JsonValueKind.False => ("boolean", false),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? ("number", l) : ("number", el.GetDouble()),
            JsonValueKind.String => ("string", el.GetString()),
            JsonValueKind.Array => ("array", null),
            JsonValueKind.Object => ("object", null),
            _ => ("string", null),
        };
    }

    private sealed class FieldAcc
    {
        public HashSet<string> Types = new();
        public bool Nullable;
        public JsonNode? Sample;
    }

    // ---- database-level info ----
    public static (bool isSolodb, string? version) IsSolodbDatabase(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'SoloDB%' LIMIT 1";
        var row = cmd.ExecuteScalar();
        if (row == null || row == DBNull.Value)
        {
            var tables = ListTables(conn);
            bool hasCollection = tables.Any(t => t.Kind == TableKind.SolodbCollection);
            return (hasCollection, null);
        }
        return (true, null);
    }

    public static DatabaseInfo GetDatabaseInfo(SqliteConnection conn, string absPath)
    {
        var tables = ListTables(conn);
        var (isSolodb, version) = IsSolodbDatabase(conn);

        string sqliteVersion = "";
        try
        {
            using var c = conn.CreateCommand();
            c.CommandText = "SELECT sqlite_version()";
            sqliteVersion = (string)c.ExecuteScalar()!;
        }
        catch { }

        long sizeBytes = 0;
        try { sizeBytes = new FileInfo(absPath).Length; } catch { }

        var pragmas = new List<PragmaInfo>();
        foreach (var name in new[] { "journal_mode", "page_size", "page_count", "freelist_count", "application_id", "user_version" })
        {
            string value = "n/a";
            try
            {
                using var c = conn.CreateCommand();
                c.CommandText = $"PRAGMA {name}";
                var res = c.ExecuteScalar();
                value = res == null || res == DBNull.Value ? "" : res.ToString()!;
            }
            catch { }
            pragmas.Add(new PragmaInfo { Name = name, Value = value });
        }

        return new DatabaseInfo
        {
            Path = absPath,
            FileName = Path.GetFileName(absPath),
            SizeBytes = sizeBytes,
            IsSolodb = isSolodb,
            SolodbVersion = version,
            SqliteVersion = sqliteVersion,
            TableCount = tables.Count,
            CollectionCount = tables.Count(t => t.Kind == TableKind.SolodbCollection),
            TotalRows = tables.Sum(t => t.RowCount),
            Tables = tables,
            Pragmas = pragmas,
        };
    }

    // ---- data access ----
    public static PageResult GetTableData(SqliteConnection conn, string table, int page, int pageSize, string? sort, string order, string? search)
    {
        var kind = DetectTableKind(conn, table);
        page = Math.Max(1, page);
        pageSize = Math.Min(500, Math.Max(1, pageSize));
        int offset = (page - 1) * pageSize;
        string ord = order == "desc" ? "DESC" : "ASC";

        return kind == TableKind.SolodbCollection
            ? GetSolodbCollectionData(conn, table, page, pageSize, offset, sort, ord, search)
            : GetRegularTableData(conn, table, page, pageSize, offset, sort, ord, search);
    }

    private static PageResult GetSolodbCollectionData(SqliteConnection conn, string table, int page, int pageSize, int offset, string? sort, string ord, string? search)
    {
        var t = QuoteIdent(table);
        var whereParts = new List<string>();
        var parms = new List<SqliteParameter>();
        if (!string.IsNullOrEmpty(search))
        {
            whereParts.Add("CAST(json(\"Value\") AS TEXT) LIKE @s");
            parms.Add(new SqliteParameter("@s", $"%{search}%"));
        }
        string where = whereParts.Count > 0 ? "WHERE " + string.Join(" AND ", whereParts) : "";

        long total;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*) FROM {t} {where}";
            foreach (var p in parms) c.Parameters.Add(p);
            total = (long)c.ExecuteScalar()!;
        }

        string orderBy = $"ORDER BY \"Id\" {ord}";
        if (!string.IsNullOrEmpty(sort))
        {
            if (sort == "Id") orderBy = $"ORDER BY \"Id\" {ord}";
            else if (Regex.IsMatch(sort, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                orderBy = $"ORDER BY json_extract(\"Value\", '$.{sort}') {ord} NULLS LAST";
        }

        var rows = new List<Dictionary<string, JsonNode?>>();
        var columns = new List<string> { "Id" };
        var colSet = new HashSet<string> { "Id" };

        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT \"Id\", {JsonTextExpr("Value")} AS __value, {JsonTextExpr("Metadata")} AS __meta FROM {t} {where} {orderBy} LIMIT @limit OFFSET @offset";
            foreach (var p in parms) c.Parameters.Add(p);
            c.Parameters.AddWithValue("@limit", pageSize);
            c.Parameters.AddWithValue("@offset", offset);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var id = r["Id"];
                var row = new Dictionary<string, JsonNode?> { ["Id"] = ToJsonNode(id) };
                JsonNode? rawValue = null;
                JsonNode? metadata = null;
                if (!r.IsDBNull("__value"))
                {
                    var vtxt = r.GetString("__value");
                    try
                    {
                        using var doc = JsonDocument.Parse(vtxt);
                        rawValue = JsonElementToNode(doc.RootElement);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var prop in doc.RootElement.EnumerateObject())
                            {
                                row[prop.Name] = JsonElementToNode(prop.Value);
                                if (colSet.Add(prop.Name)) columns.Add(prop.Name);
                            }
                        }
                    }
                    catch { }
                }
                if (!r.IsDBNull("__meta"))
                {
                    var mtxt = r.GetString("__meta");
                    try { using var md = JsonDocument.Parse(mtxt); metadata = JsonElementToNode(md.RootElement); }
                    catch { metadata = mtxt; }
                }
                row["__identifier"] = new JsonObject { ["Id"] = ToJsonNode(id) };
                row["__rawValue"] = rawValue;
                row["__metadata"] = metadata;
                rows.Add(row);
            }
        }

        // Add inferred document fields to the column list (stable order)
        var fields = GetDocumentFields(conn, table);
        foreach (var f in fields)
        {
            if (colSet.Add(f.Key)) columns.Add(f.Key);
        }

        return new PageResult
        {
            Rows = rows,
            Columns = columns,
            Total = total,
            Page = page,
            PageSize = pageSize,
            IsSolodbCollection = true,
        };
    }

    private static PageResult GetRegularTableData(SqliteConnection conn, string table, int page, int pageSize, int offset, string? sort, string ord, string? search)
    {
        var t = QuoteIdent(table);
        var cols = GetTableColumns(conn, table);
        var colNames = cols.Select(c => c.Name).ToList();
        var pk = GetPkColumns(conn, table);
        bool hasRowid = !cols.Any(c => c.Name.Equals("rowid", StringComparison.OrdinalIgnoreCase));

        var whereParts = new List<string>();
        var parms = new List<SqliteParameter>();
        if (!string.IsNullOrEmpty(search))
        {
            var textCols = cols.Where(c => string.IsNullOrEmpty(c.Type) || Regex.IsMatch(c.Type, "char|text|clob|json", RegexOptions.IgnoreCase)).ToList();
            if (textCols.Count > 0)
            {
                var ors = textCols.Select(c => $"CAST({QuoteIdent(c.Name)} AS TEXT) LIKE @s_{textCols.IndexOf(c)}");
                whereParts.Add("(" + string.Join(" OR ", ors) + ")");
                for (int i = 0; i < textCols.Count; i++)
                    parms.Add(new SqliteParameter($"@s_{i}", $"%{search}%"));
            }
        }
        string where = whereParts.Count > 0 ? "WHERE " + string.Join(" AND ", whereParts) : "";

        long total;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*) FROM {t} {where}";
            foreach (var p in parms) c.Parameters.Add(p);
            total = (long)c.ExecuteScalar()!;
        }

        string orderBy = "";
        if (!string.IsNullOrEmpty(sort) && colNames.Contains(sort))
            orderBy = $"ORDER BY {QuoteIdent(sort)} {ord} NULLS LAST";
        else if (pk.Count > 0)
            orderBy = $"ORDER BY {string.Join(", ", pk.Select(QuoteIdent))} {ord}";
        else if (hasRowid)
            orderBy = $"ORDER BY rowid {ord}";

        var selectCols = new List<string>();
        if (hasRowid) selectCols.Add("rowid AS __rowid");
        selectCols.AddRange(colNames.Select(QuoteIdent));

        var rows = new List<Dictionary<string, JsonNode?>>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = $"SELECT {string.Join(", ", selectCols)} FROM {t} {where} {orderBy} LIMIT @limit OFFSET @offset";
            foreach (var p in parms) c.Parameters.Add(p);
            c.Parameters.AddWithValue("@limit", pageSize);
            c.Parameters.AddWithValue("@offset", offset);
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var row = new Dictionary<string, JsonNode?>();
                JsonNode? rowid = null;
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var name = r.GetName(i);
                    var val = r.IsDBNull(i) ? null : r.GetValue(i);
                    if (name == "__rowid") { rowid = ToJsonNode(val); continue; }
                    row[name] = ToJsonNode(val);
                }
                var identifier = new JsonObject();
                if (pk.Count > 0) foreach (var k in pk) identifier[k] = row.GetValueOrDefault(k);
                else identifier["rowid"] = rowid;
                row["__rowid"] = rowid;
                row["__identifier"] = identifier;
                rows.Add(row);
            }
        }

        return new PageResult
        {
            Rows = rows,
            Columns = colNames,
            Total = total,
            Page = page,
            PageSize = pageSize,
            IsSolodbCollection = false,
        };
    }

    // ---- mutations ----
    public static long InsertSolodbDocument(SqliteConnection conn, string table, JsonNode? value, JsonNode? metadata = null)
    {
        var t = QuoteIdent(table);
        var valueJson = value?.ToJsonString() ?? "null";
        var metaJson = metadata?.ToJsonString() ?? "{}";
        using var c = conn.CreateCommand();
        c.CommandText = $"INSERT INTO {t} (\"Value\", \"Metadata\") VALUES (jsonb(@v), jsonb(@m))";
        c.Parameters.AddWithValue("@v", valueJson);
        c.Parameters.AddWithValue("@m", metaJson);
        c.ExecuteNonQuery();
        using var c2 = conn.CreateCommand();
        c2.CommandText = "SELECT last_insert_rowid()";
        return (long)c2.ExecuteScalar()!;
    }

    public static int UpdateSolodbDocument(SqliteConnection conn, string table, JsonNode id, JsonNode? value, JsonNode? metadata = null)
    {
        var t = QuoteIdent(table);
        var valueJson = value?.ToJsonString() ?? "null";
        using var c = conn.CreateCommand();
        if (metadata != null)
        {
            var metaJson = metadata.ToJsonString();
            c.CommandText = $"UPDATE {t} SET \"Value\" = jsonb(@v), \"Metadata\" = jsonb(@m) WHERE \"Id\" = @id";
            c.Parameters.AddWithValue("@m", metaJson);
        }
        else
        {
            c.CommandText = $"UPDATE {t} SET \"Value\" = jsonb(@v) WHERE \"Id\" = @id";
        }
        c.Parameters.AddWithValue("@v", valueJson);
        c.Parameters.AddWithValue("@id", JsonNodeToDbValue(id));
        return c.ExecuteNonQuery();
    }

    // Convert a JsonNode (from the request body) to a DB parameter value.
    private static object? JsonNodeToDbValue(JsonNode? node)
    {
        if (node == null) return DBNull.Value;
        return node.GetValueKind() switch
        {
            JsonValueKind.Null => DBNull.Value,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => node.AsValue().TryGetValue<long>(out var l) ? l : node.GetValue<double>(),
            JsonValueKind.String => node.GetValue<string>(),
            _ => node.ToJsonString(),
        };
    }

    // Convert any reader value to a JsonNode for the response.
    private static JsonNode? ToJsonNode(object? v)
    {
        if (v == null || v == DBNull.Value) return null;
        if (v is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        if (v is bool b) return b;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is double d) return d;
        if (v is decimal dec) return (double)dec;
        if (v is string s) return s;
        if (v is DateTime dt) return dt.ToString("o");
        if (v is DateTimeOffset dto) return dto.ToString("o");
        if (v is Guid g) return g.ToString();
        return v.ToString();
    }

    public static int DeleteSolodbDocument(SqliteConnection conn, string table, JsonNode id)
    {
        var t = QuoteIdent(table);
        using var c = conn.CreateCommand();
        c.CommandText = $"DELETE FROM {t} WHERE \"Id\" = @id";
        c.Parameters.AddWithValue("@id", JsonNodeToDbValue(id));
        return c.ExecuteNonQuery();
    }

    public static (long rowid, int changes) InsertRegularRow(SqliteConnection conn, string table, Dictionary<string, JsonNode?> values)
    {
        var t = QuoteIdent(table);
        var cols = values.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();
        if (cols.Count == 0)
        {
            using var c0 = conn.CreateCommand();
            c0.CommandText = $"INSERT INTO {t} DEFAULT VALUES";
            c0.ExecuteNonQuery();
        }
        else
        {
            var ph = string.Join(", ", cols.Select((_, i) => "@p" + i));
            var colList = string.Join(", ", cols.Select(QuoteIdent));
            using var c = conn.CreateCommand();
            c.CommandText = $"INSERT INTO {t} ({colList}) VALUES ({ph})";
            for (int i = 0; i < cols.Count; i++)
                c.Parameters.AddWithValue("@p" + i, JsonNodeToDbValue(values[cols[i]]));
            c.ExecuteNonQuery();
        }
        using var c2 = conn.CreateCommand();
        c2.CommandText = "SELECT last_insert_rowid()";
        return ((long)c2.ExecuteScalar()!, 1);
    }

    public static int UpdateRegularRow(SqliteConnection conn, string table, Dictionary<string, JsonNode?> identifier, Dictionary<string, JsonNode?> values)
    {
        var t = QuoteIdent(table);
        var setCols = values.Where(kv => kv.Value != null).Select(kv => kv.Key).ToList();
        if (setCols.Count == 0) return 0;
        var setClause = string.Join(", ", setCols.Select((c, i) => $"{QuoteIdent(c)} = @s{i}"));
        var idCols = identifier.Keys.ToList();
        var whereClause = string.Join(" AND ", idCols.Select((c, i) => $"{QuoteIdent(c)} = @w{i}"));
        using var c = conn.CreateCommand();
        c.CommandText = $"UPDATE {t} SET {setClause} WHERE {whereClause}";
        for (int i = 0; i < setCols.Count; i++) c.Parameters.AddWithValue("@s" + i, JsonNodeToDbValue(values[setCols[i]]));
        for (int i = 0; i < idCols.Count; i++) c.Parameters.AddWithValue("@w" + i, JsonNodeToDbValue(identifier[idCols[i]]));
        return c.ExecuteNonQuery();
    }

    public static int DeleteRegularRow(SqliteConnection conn, string table, Dictionary<string, JsonNode?> identifier)
    {
        var t = QuoteIdent(table);
        var idCols = identifier.Keys.ToList();
        if (idCols.Count == 0) return 0;
        var whereClause = string.Join(" AND ", idCols.Select((c, i) => $"{QuoteIdent(c)} = @w{i}"));
        using var c = conn.CreateCommand();
        c.CommandText = $"DELETE FROM {t} WHERE {whereClause}";
        for (int i = 0; i < idCols.Count; i++) c.Parameters.AddWithValue("@w" + i, JsonNodeToDbValue(identifier[idCols[i]]));
        return c.ExecuteNonQuery();
    }

    public static int DropTable(SqliteConnection conn, string table)
    {
        var t = QuoteIdent(table);
        using var c = conn.CreateCommand();
        c.CommandText = $"DROP TABLE IF EXISTS {t}";
        return c.ExecuteNonQuery();
    }

    public static void CreateSolodbCollection(SqliteConnection conn, string name)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("Invalid collection name");
        if (name.StartsWith("SoloDB"))
            throw new ArgumentException("Collection names starting with 'SoloDB' are reserved");
        using var c = conn.CreateCommand();
        c.CommandText = $"CREATE TABLE IF NOT EXISTS {QuoteIdent(name)} (\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, \"Value\" JSONB, \"Metadata\" JSONB)";
        c.ExecuteNonQuery();
    }

    public static void CreateRegularTable(SqliteConnection conn, string name, List<CreateColumnSpec> columns)
    {
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new ArgumentException("Invalid table name");
        if (columns.Count == 0) throw new ArgumentException("At least one column is required");
        var defs = columns.Select(c =>
        {
            var parts = new List<string> { QuoteIdent(c.Name), string.IsNullOrEmpty(c.Type) ? "TEXT" : c.Type };
            if (c.PrimaryKey) parts.Add("PRIMARY KEY");
            if (c.NotNull) parts.Add("NOT NULL");
            return string.Join(" ", parts);
        });
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {QuoteIdent(name)} ({string.Join(", ", defs)})";
        cmd.ExecuteNonQuery();
    }

    // ---- raw query ----
    public static QueryResult ExecuteQuery(SqliteConnection conn, string sql)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var trimmed = sql.TrimStart();
            bool returnsRows = Regex.IsMatch(trimmed, @"^\s*(SELECT|PRAGMA|WITH|EXPLAIN|VALUES)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(trimmed, @"\bRETURNING\b", RegexOptions.IgnoreCase);
            if (returnsRows)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                using var r = cmd.ExecuteReader();
                var cols = new List<string>();
                for (int i = 0; i < r.FieldCount; i++) cols.Add(r.GetName(i));
                var rows = new List<List<JsonNode?>>();
                while (r.Read())
                {
                    var row = new List<JsonNode?>();
                    for (int i = 0; i < r.FieldCount; i++)
                        row.Add(r.IsDBNull(i) ? null : ToJsonNode(r.GetValue(i)));
                    rows.Add(row);
                }
                sw.Stop();
                return new QueryResult { Columns = cols, Rows = rows, RowsAffected = rows.Count, DurationMs = sw.ElapsedMilliseconds };
            }
            // executable (possibly multiple statements)
            long before = TotalChanges(conn);
            using (var cmd = conn.CreateCommand()) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
            long after = TotalChanges(conn);
            sw.Stop();
            return new QueryResult { Columns = new(), Rows = new(), RowsAffected = (int)(after - before), DurationMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new QueryResult { Columns = new(), Rows = new(), RowsAffected = 0, DurationMs = sw.ElapsedMilliseconds, Error = ex.Message };
        }
    }

    private static long TotalChanges(SqliteConnection conn)
    {
        using var c = conn.CreateCommand();
        c.CommandText = "SELECT total_changes()";
        return (long)c.ExecuteScalar()!;
    }

    private static object? NormalizeCell(object v)
    {
        if (v == null || v == DBNull.Value) return null;
        if (v is byte[] bytes) return Encoding.UTF8.GetString(bytes);
        return v;
    }

    // ---- JSON helpers ----
    public static JsonNode? JsonElementToNode(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Array => new JsonArray(el.EnumerateArray().Select(JsonElementToNode).ToArray()),
            JsonValueKind.Object => new JsonObject(el.EnumerateObject().Select(p => new KeyValuePair<string, JsonNode?>(p.Name, JsonElementToNode(p.Value))), null),
            _ => el.GetRawText(),
        };
    }
}
