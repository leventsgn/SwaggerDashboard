namespace SwaggerDashboard.Application.Dashboards;

/// <summary>
/// The pre-rendered dashboard model persisted as <c>ApiDefinition.DashboardJson</c>.
/// It is produced once per swagger hash and read back verbatim on every later visit,
/// so the shape is versioned: bump <see cref="CurrentSchemaVersion"/> whenever the
/// generator starts producing something older stored documents cannot represent.
/// </summary>
public class DashboardDocument
{
    /// <summary>Increment when the generator output shape changes.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string Title { get; set; } = string.Empty;

    public string? Version { get; set; }

    public string? Description { get; set; }

    public string? OpenApiVersion { get; set; }

    /// <summary>Server URLs declared by the document, absolute where resolvable.</summary>
    public List<string> Servers { get; set; } = [];

    public List<DashboardSecurityScheme> SecuritySchemes { get; set; } = [];

    public List<DashboardTag> Tags { get; set; } = [];

    public List<DashboardOperation> Operations { get; set; } = [];

    /// <summary>Non fatal problems encountered while parsing, shown to administrators.</summary>
    public List<string> Warnings { get; set; } = [];
}

public class DashboardTag
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Slugs of the operations grouped under this tag, in document order.</summary>
    public List<string> OperationSlugs { get; set; } = [];
}

public class DashboardSecurityScheme
{
    public string Key { get; set; } = string.Empty;

    /// <summary>apiKey, http, oauth2 or openIdConnect.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>For http schemes: bearer, basic and so on.</summary>
    public string? Scheme { get; set; }

    /// <summary>For apiKey schemes: header, query or cookie.</summary>
    public string? In { get; set; }

    /// <summary>For apiKey schemes: the header or query parameter name.</summary>
    public string? ParameterName { get; set; }

    public string? Description { get; set; }
}

public class DashboardOperation
{
    /// <summary>Stable URL safe identifier, unique inside the document.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    /// <summary>Path template as written in the document, e.g. /customers/{id}.</summary>
    public string Path { get; set; } = string.Empty;

    public string? OperationId { get; set; }

    public string? Summary { get; set; }

    public string? Description { get; set; }

    public string Tag { get; set; } = DashboardConstants.DefaultTag;

    public bool Deprecated { get; set; }

    public List<DashboardParameter> Parameters { get; set; } = [];

    public DashboardRequestBody? RequestBody { get; set; }

    public List<DashboardResponse> Responses { get; set; } = [];

    /// <summary>Security scheme keys required by this operation.</summary>
    public List<string> SecuritySchemeKeys { get; set; } = [];

    public bool RequiresAuthentication => SecuritySchemeKeys.Count > 0;
}

public class DashboardParameter
{
    public string Name { get; set; } = string.Empty;

    /// <summary>path, query, header or cookie.</summary>
    public string In { get; set; } = ParameterLocations.Query;

    public bool Required { get; set; }

    public bool Deprecated { get; set; }

    public string? Description { get; set; }

    /// <summary>OpenAPI serialization style, e.g. form, simple, deepObject.</summary>
    public string? Style { get; set; }

    public bool Explode { get; set; }

    public FieldSchema Schema { get; set; } = new();
}

public class DashboardRequestBody
{
    public bool Required { get; set; }

    public string? Description { get; set; }

    /// <summary>Content types offered by the document, preferred one first.</summary>
    public List<DashboardContent> Contents { get; set; } = [];
}

public class DashboardContent
{
    public string ContentType { get; set; } = "application/json";

    public FieldSchema Schema { get; set; } = new();

    public string? ExampleJson { get; set; }
}

public class DashboardResponse
{
    public string StatusCode { get; set; } = "200";

    public string? Description { get; set; }

    public List<DashboardContent> Contents { get; set; } = [];
}

/// <summary>
/// A normalized, UI ready description of a value. Recursive documents are handled by
/// <see cref="RefName"/> plus <see cref="Truncated"/>: the generator stops descending
/// and the form offers to expand the node on demand.
/// </summary>
public class FieldSchema
{
    /// <summary>string, number, integer, boolean, array, object or unknown.</summary>
    public string Type { get; set; } = SchemaTypes.Unknown;

    /// <summary>OpenAPI format hint such as date-time, uuid, binary, email or password.</summary>
    public string? Format { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    public bool Nullable { get; set; }

    public List<string> Enum { get; set; } = [];

    public string? Default { get; set; }

    public string? Example { get; set; }

    public string? Pattern { get; set; }

    public decimal? Minimum { get; set; }

    public decimal? Maximum { get; set; }

    public int? MinLength { get; set; }

    public int? MaxLength { get; set; }

    public bool ReadOnly { get; set; }

    public bool WriteOnly { get; set; }

    /// <summary>Component name when the schema came from a $ref, used to label recursion.</summary>
    public string? RefName { get; set; }

    /// <summary>True when descent stopped because of recursion or the depth limit.</summary>
    public bool Truncated { get; set; }

    /// <summary>Item schema when <see cref="Type"/> is array.</summary>
    public FieldSchema? Items { get; set; }

    /// <summary>Properties when <see cref="Type"/> is object.</summary>
    public List<FieldProperty> Properties { get; set; } = [];

    /// <summary>Schema for free form additional properties, when allowed.</summary>
    public FieldSchema? AdditionalProperties { get; set; }

    /// <summary>
    /// Alternative shapes from oneOf/anyOf. The form renders a variant selector when
    /// this is non empty; <see cref="DiscriminatorProperty"/> names the deciding field.
    /// </summary>
    public List<FieldVariant> Variants { get; set; } = [];

    public string? DiscriminatorProperty { get; set; }
}

public class FieldProperty
{
    public string Name { get; set; } = string.Empty;

    public bool Required { get; set; }

    public FieldSchema Schema { get; set; } = new();
}

public class FieldVariant
{
    public string Name { get; set; } = string.Empty;

    public FieldSchema Schema { get; set; } = new();
}

public static class SchemaTypes
{
    public const string String = "string";
    public const string Number = "number";
    public const string Integer = "integer";
    public const string Boolean = "boolean";
    public const string Array = "array";
    public const string Object = "object";
    public const string Unknown = "unknown";
}

public static class ParameterLocations
{
    public const string Path = "path";
    public const string Query = "query";
    public const string Header = "header";
    public const string Cookie = "cookie";
}

public static class DashboardConstants
{
    public const string DefaultTag = "default";

    /// <summary>How deep the generator descends into nested schemas before truncating.</summary>
    public const int MaxSchemaDepth = 8;
}
