using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SoloDbManager;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ConnectionsResponse))]
[JsonSerializable(typeof(ConnectionResponse))]
[JsonSerializable(typeof(InfoResponse))]
[JsonSerializable(typeof(TablesResponse))]
[JsonSerializable(typeof(SchemaResponse))]
[JsonSerializable(typeof(DataResponse))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(OkResponse))]
[JsonSerializable(typeof(FilesResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(BrowseResponse))]
[JsonSerializable(typeof(List<BrowseEntry>))]
[JsonSerializable(typeof(OpenRequest))]
[JsonSerializable(typeof(CreateTableRequest))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(InsertRequest))]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(DeleteRequest))]
[JsonSerializable(typeof(ConnectionRequest))]
[JsonSerializable(typeof(List<CreateColumnSpec>))]
[JsonSerializable(typeof(Dictionary<string, JsonNode?>))]
[JsonSerializable(typeof(List<Dictionary<string, JsonNode?>>))]
[JsonSerializable(typeof(List<JsonNode?>))]
[JsonSerializable(typeof(List<List<JsonNode?>>))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
