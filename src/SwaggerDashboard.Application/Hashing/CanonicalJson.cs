using System.Buffers;
using System.Text;
using System.Text.Json;

namespace SwaggerDashboard.Application.Hashing;

/// <summary>
/// Deterministic JSON serialization used as the input to the swagger hash.
/// </summary>
/// <remarks>
/// Without this the hash would change whenever the target API reorders its object keys or
/// reformats whitespace, and every refresh would rebuild the dashboard for no reason.
/// Object members are emitted in ordinal order of their names, arrays keep their order
/// because array order is meaningful in OpenAPI, and all insignificant whitespace is
/// removed. Numbers are written from their raw text so that 1.0 and 1 stay distinct
/// rather than being folded by a round trip through double.
/// </remarks>
public static class CanonicalJson
{
    /// <summary>
    /// Canonicalizes a JSON document, or reports that the text is not JSON.
    /// </summary>
    /// <remarks>
    /// An OpenAPI document may be YAML. The reader accepts it, so the platform must too:
    /// letting the parse exception escape from hashing turned a supported document format
    /// into a dead page.
    /// </remarks>
    public static bool TryCanonicalize(string text, out string? canonical)
    {
        try
        {
            canonical = Canonicalize(text);
            return true;
        }
        catch (JsonException)
        {
            canonical = null;
            return false;
        }
    }

    public static string Canonicalize(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            MaxDepth = 256,
        });

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            case JsonValueKind.True:
            case JsonValueKind.False:
                writer.WriteBooleanValue(element.GetBoolean());
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }
}
