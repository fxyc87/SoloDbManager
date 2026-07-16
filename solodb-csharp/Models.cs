using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SoloDbManager;

[JsonConverter(typeof(TableKindConverter))]
public enum TableKind
{
    [JsonPropertyName("solodb-collection")] SolodbCollection,
    [JsonPropertyName("solodb-internal")] SolodbInternal,
    [JsonPropertyName("regular")] Regular,
}

public sealed class ColumnInfo
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool NotNull { get; set; }
    public string? DefaultValue { get; set; }
    public bool PrimaryKey { get; set; }
    public int Hidden { get; set; }
}

public sealed class IndexInfo
{
    public string Name { get; set; } = "";
    public List<string> Columns { get; set; } = new();
    public bool Unique { get; set; }
    public string? Sql { get; set; }
    public bool Partial { get; set; }
}

public sealed class DocumentField
{
    public string Key { get; set; } = "";
    public string JsType { get; set; } = "";
    public bool Nullable { get; set; }
    public JsonNode? Sample { get; set; }
}

public class TableSummary
{
    public string Name { get; set; } = "";
    public TableKind Kind { get; set; }
    public long RowCount { get; set; }
    public long? SizeBytes { get; set; }
}

public sealed class TableSchema : TableSummary
{
    public List<ColumnInfo> Columns { get; set; } = new();
    public List<IndexInfo> Indexes { get; set; } = new();
    public string? CreateSql { get; set; }
    public List<DocumentField> DocumentFields { get; set; } = new();
}

public sealed class PragmaInfo
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class DatabaseInfo
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsSolodb { get; set; }
    public string? SolodbVersion { get; set; }
    public string SqliteVersion { get; set; } = "";
    public int TableCount { get; set; }
    public int CollectionCount { get; set; }
    public long TotalRows { get; set; }
    public List<TableSummary> Tables { get; set; } = new();
    public List<PragmaInfo> Pragmas { get; set; } = new();
}

public sealed class Connection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsSolodb { get; set; }
    public string LastOpenedAt { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

public sealed class PageResult
{
    public List<Dictionary<string, JsonNode?>> Rows { get; set; } = new();
    public List<string> Columns { get; set; } = new();
    public long Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public bool IsSolodbCollection { get; set; }
}

public sealed class QueryResult
{
    public List<string> Columns { get; set; } = new();
    public List<List<JsonNode?>> Rows { get; set; } = new();
    public int RowsAffected { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
}

public sealed class StoredConnection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int IsSolodb { get; set; }
    public string LastOpenedAt { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

// --- Request bodies ---

public sealed class OpenRequest
{
    public string? Path { get; set; }
    public bool? Create { get; set; }
}

public sealed class CreateTableRequest
{
    public string? Db { get; set; }
    public string? Name { get; set; }
    public TableKind Kind { get; set; }
    public List<CreateColumnSpec>? Columns { get; set; }
}

public sealed class CreateColumnSpec
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool PrimaryKey { get; set; }
    public bool NotNull { get; set; }
}

public sealed class QueryRequest
{
    public string? Db { get; set; }
    public string? Sql { get; set; }
}

public sealed class InsertRequest
{
    public JsonNode? Value { get; set; }
    public JsonNode? Metadata { get; set; }
    public Dictionary<string, JsonNode?>? Values { get; set; }
}

public sealed class UpdateRequest
{
    public Dictionary<string, JsonNode?>? Identifier { get; set; }
    public Dictionary<string, JsonNode?>? Values { get; set; }
    public JsonNode? Value { get; set; }
    public JsonNode? Metadata { get; set; }
}

public sealed class DeleteRequest
{
    public Dictionary<string, JsonNode?>? Identifier { get; set; }
}

public sealed class ConnectionRequest
{
    public string? Name { get; set; }
    public string? Path { get; set; }
}

// --- Response wrappers ---

public sealed class ConnectionsResponse { public List<Connection> Connections { get; set; } = new(); }
public sealed class ConnectionResponse { public Connection Connection { get; set; } = new(); }
public sealed class InfoResponse { public DatabaseInfo Info { get; set; } = new(); }
public sealed class TablesResponse { public List<TableSummary> Tables { get; set; } = new(); }
public sealed class SchemaResponse { public TableSchema Schema { get; set; } = new(); }
public sealed class DataResponse { public PageResult Data { get; set; } = new(); }
public sealed class QueryResponse { public QueryResult Result { get; set; } = new(); }
public sealed class OkResponse { public bool Ok { get; set; } public string? Name { get; set; } public JsonNode? Id { get; set; } public JsonNode? Rowid { get; set; } public int Changes { get; set; } }
public sealed class FileEntry { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public long SizeBytes { get; set; } public string Modified { get; set; } = ""; }
public sealed class FilesResponse { public List<FileEntry> Files { get; set; } = new(); public string Dir { get; set; } = ""; }
public sealed class ErrorResponse { public string Error { get; set; } = ""; }

// Local file-system browsing (for the "Browse Local" tab — open databases
// directly by path without uploading them to the databases/ folder).
public sealed class BrowseEntry { public string Name { get; set; } = ""; public string Path { get; set; } = ""; public bool IsDir { get; set; } public long? Size { get; set; } }
public sealed class BrowseResponse { public string Current { get; set; } = ""; public string? Parent { get; set; } public List<BrowseEntry> Entries { get; set; } = new(); public string? Error { get; set; } }
