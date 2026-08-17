using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SwaggerDashboard.Application.Execution;

/// <summary>
/// The part of a filled in request that is worth storing and can be restored later.
/// </summary>
/// <remarks>
/// Deliberately values rather than a serialized form tree: the tree is derived from the
/// swagger document, which changes under us. Storing values means a saved request still loads
/// after the document gains or loses a field — the fields that still exist are filled and the
/// rest is ignored, instead of the whole thing failing to deserialize.
///
/// Credentials are never part of this. They live in server memory for the session and a saved
/// request is a database row; putting a bearer token in one would turn a convenience feature
/// into a credential store.
/// </remarks>
public record SavedRequestPayload
{
    public const int CurrentVersion = 1;

    /// <summary>Guards against reading a payload written by a later, different shape.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Parameter values keyed by "in:name". A scalar is a single element list.</summary>
    public Dictionary<string, List<string>> Parameters { get; init; } = new(StringComparer.Ordinal);

    public string? ContentType { get; init; }

    /// <summary>Request body as JSON text, exactly as it would be sent.</summary>
    public string? Body { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        // Turkish text in a saved body should stay readable in the database, the same way it
        // does in the raw view and the request log.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Reads a stored payload, or null when the row cannot be understood.</summary>
    public static SavedRequestPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SavedRequestPayload>(json, Options);

            return payload is null || payload.Version > CurrentVersion ? null : payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads the body as a JSON node, or null when it is absent or not JSON.</summary>
    public JsonNode? BodyAsNode()
    {
        if (string.IsNullOrWhiteSpace(Body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(Body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
