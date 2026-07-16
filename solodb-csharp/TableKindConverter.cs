using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoloDbManager;

/// <summary>
/// Custom converter for TableKind that emits the hyphenated string values
/// ("solodb-collection" / "solodb-internal" / "regular") expected by the
/// frontend, since JsonStringEnumConverter ignores [JsonPropertyName].
/// </summary>
public sealed class TableKindConverter : JsonConverter<TableKind>
{
    public override TableKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        return s switch
        {
            "solodb-collection" => TableKind.SolodbCollection,
            "solodb-internal" => TableKind.SolodbInternal,
            "regular" => TableKind.Regular,
            "SolodbCollection" => TableKind.SolodbCollection,
            "SolodbInternal" => TableKind.SolodbInternal,
            "Regular" => TableKind.Regular,
            _ => TableKind.Regular,
        };
    }

    public override void Write(Utf8JsonWriter writer, TableKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            TableKind.SolodbCollection => "solodb-collection",
            TableKind.SolodbInternal => "solodb-internal",
            TableKind.Regular => "regular",
            _ => "regular",
        });
    }
}
