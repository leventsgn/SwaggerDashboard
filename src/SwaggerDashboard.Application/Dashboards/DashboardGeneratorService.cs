using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Routing;

namespace SwaggerDashboard.Application.Dashboards;

/// <summary>
/// Parses an OpenAPI document and flattens it into the dashboard model.
/// </summary>
public class DashboardGeneratorService : IDashboardGeneratorService
{
    private readonly ILogger<DashboardGeneratorService> _logger;

    public DashboardGeneratorService(ILogger<DashboardGeneratorService> logger)
    {
        _logger = logger;
    }

    public DashboardGenerationResult Generate(string openApiContent)
    {
        if (string.IsNullOrWhiteSpace(openApiContent))
        {
            return DashboardGenerationResult.Fail("OpenAPI dokümanı boş.");
        }

        OpenApiDocument document;
        OpenApiDiagnostic diagnostic;

        try
        {
            document = new OpenApiStringReader().Read(openApiContent, out diagnostic);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAPI document could not be parsed");
            return DashboardGenerationResult.Fail($"OpenAPI dokümanı ayrıştırılamadı: {ex.Message}");
        }

        if (document.Paths is null || document.Paths.Count == 0)
        {
            var parseError = diagnostic.Errors.Count > 0
                ? diagnostic.Errors[0].Message
                : "doküman hiç path içermiyor";
            return DashboardGenerationResult.Fail($"OpenAPI dokümanı kullanılabilir değil: {parseError}");
        }

        var dashboard = new DashboardDocument
        {
            SchemaVersion = DashboardDocument.CurrentSchemaVersion,
            Title = document.Info?.Title ?? "API",
            Version = document.Info?.Version,
            Description = document.Info?.Description,
            OpenApiVersion = diagnostic.SpecificationVersion.ToString(),
        };

        foreach (var error in diagnostic.Errors.Take(20))
        {
            dashboard.Warnings.Add(error.Message);
        }

        if (document.Servers is not null)
        {
            foreach (var server in document.Servers.Where(s => !string.IsNullOrWhiteSpace(s.Url)))
            {
                dashboard.Servers.Add(new DashboardServer
                {
                    Url = ResolveServerUrl(server).TrimEnd('/'),
                    Description = server.Description,
                });
            }
        }

        AddSecuritySchemes(document, dashboard);

        var tagDescriptions = document.Tags?
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .ToDictionary(t => t.Name, t => (string?)t.Description, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var documentSecurityKeys = ExtractSecurityKeys(document.SecurityRequirements);
        var takenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, pathItem) in document.Paths)
        {
            if (pathItem?.Operations is null)
            {
                continue;
            }

            foreach (var (operationType, operation) in pathItem.Operations)
            {
                var method = operationType.ToString().ToUpperInvariant();
                var dashboardOperation = BuildOperation(
                    method, path, operation, pathItem, documentSecurityKeys, takenSlugs, dashboard);

                dashboard.Operations.Add(dashboardOperation);
            }
        }

        BuildTags(dashboard, tagDescriptions);

        return DashboardGenerationResult.Ok(dashboard);
    }

    /// <summary>
    /// Substitutes the declared defaults into a templated server URL.
    /// </summary>
    /// <remarks>
    /// A URL like http://{host}:{port}/{basePath} is fully usable when every variable has a
    /// default — which is the common case. Leaving it templated made the platform fall back to
    /// the address the document was served from, so calls quietly went to the wrong host. A
    /// variable without a default keeps its placeholder: guessing one would be worse than
    /// leaving the base address to be set by hand.
    /// </remarks>
    private static string ResolveServerUrl(OpenApiServer server)
    {
        var url = server.Url;

        if (server.Variables is null || server.Variables.Count == 0 || !url.Contains('{'))
        {
            return url;
        }

        foreach (var (name, variable) in server.Variables)
        {
            if (string.IsNullOrEmpty(variable?.Default))
            {
                continue;
            }

            url = url.Replace($"{{{name}}}", variable.Default, StringComparison.Ordinal);
        }

        return url;
    }

    private static void AddSecuritySchemes(OpenApiDocument document, DashboardDocument dashboard)
    {
        if (document.Components?.SecuritySchemes is null)
        {
            return;
        }

        foreach (var (key, scheme) in document.Components.SecuritySchemes)
        {
            // Only the client credentials flow is read: it is the one an unattended test tool
            // can complete on its own. The others need a browser redirect and a human at it.
            var clientCredentials = scheme.Flows?.ClientCredentials;

            dashboard.SecuritySchemes.Add(new DashboardSecurityScheme
            {
                Key = key,
                Type = scheme.Type.ToString(),
                Scheme = scheme.Scheme,
                In = scheme.Type == SecuritySchemeType.ApiKey ? scheme.In.ToString().ToLowerInvariant() : null,
                ParameterName = scheme.Type == SecuritySchemeType.ApiKey ? scheme.Name : null,
                TokenUrl = clientCredentials?.TokenUrl?.ToString(),
                Scopes = clientCredentials?.Scopes?.Keys.ToList() ?? [],
                Description = scheme.Description,
            });
        }
    }

    private DashboardOperation BuildOperation(
        string method,
        string path,
        OpenApiOperation operation,
        OpenApiPathItem pathItem,
        List<string> documentSecurityKeys,
        HashSet<string> takenSlugs,
        DashboardDocument dashboard)
    {
        var tag = operation.Tags?.FirstOrDefault()?.Name;
        if (string.IsNullOrWhiteSpace(tag))
        {
            tag = DeriveTagFromPath(path);
        }

        var securityKeys = operation.Security is { Count: > 0 }
            ? ExtractSecurityKeys(operation.Security)
            : documentSecurityKeys;

        var result = new DashboardOperation
        {
            Slug = SlugGenerator.Create(method, path, operation.OperationId, takenSlugs),
            Method = method,
            Path = path,
            OperationId = operation.OperationId,
            Summary = operation.Summary,
            Description = operation.Description,
            Tag = tag,
            Deprecated = operation.Deprecated,
            SecuritySchemeKeys = securityKeys,
        };

        // Path level parameters apply to every operation unless the operation overrides
        // them by name and location.
        var parameters = new List<OpenApiParameter>();
        if (pathItem.Parameters is not null)
        {
            parameters.AddRange(pathItem.Parameters);
        }

        if (operation.Parameters is not null)
        {
            foreach (var parameter in operation.Parameters)
            {
                parameters.RemoveAll(p =>
                    string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) && p.In == parameter.In);
                parameters.Add(parameter);
            }
        }

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            result.Parameters.Add(new DashboardParameter
            {
                Name = parameter.Name,
                In = parameter.In?.ToString().ToLowerInvariant() ?? ParameterLocations.Query,
                Required = parameter.Required || parameter.In == ParameterLocation.Path,
                Deprecated = parameter.Deprecated,
                Description = parameter.Description,
                Style = parameter.Style?.ToString(),
                Explode = parameter.Explode,
                Schema = ConvertSchema(parameter.Schema, new SchemaContext(dashboard)),
            });
        }

        if (operation.RequestBody is not null)
        {
            result.RequestBody = new DashboardRequestBody
            {
                Required = operation.RequestBody.Required,
                Description = operation.RequestBody.Description,
                Contents = ConvertContents(operation.RequestBody.Content, dashboard),
            };
        }

        if (operation.Responses is not null)
        {
            foreach (var (statusCode, response) in operation.Responses)
            {
                result.Responses.Add(new DashboardResponse
                {
                    StatusCode = statusCode,
                    Description = response?.Description,
                    Contents = ConvertContents(response?.Content, dashboard),
                });
            }
        }

        return result;
    }

    private static List<DashboardContent> ConvertContents(
        IDictionary<string, OpenApiMediaType>? content,
        DashboardDocument dashboard)
    {
        var results = new List<DashboardContent>();
        if (content is null)
        {
            return results;
        }

        foreach (var (contentType, mediaType) in content)
        {
            results.Add(new DashboardContent
            {
                ContentType = contentType,
                Schema = ConvertSchema(mediaType?.Schema, new SchemaContext(dashboard)),
                ExampleJson = RenderExample(mediaType?.Example),
            });
        }

        // JSON first: the form view is built from it and it is what most callers want.
        return results
            .OrderByDescending(c => c.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static void BuildTags(DashboardDocument dashboard, IDictionary<string, string?> tagDescriptions)
    {
        foreach (var operation in dashboard.Operations)
        {
            var tag = dashboard.Tags.FirstOrDefault(t =>
                string.Equals(t.Name, operation.Tag, StringComparison.OrdinalIgnoreCase));

            if (tag is null)
            {
                tag = new DashboardTag
                {
                    Name = operation.Tag,
                    Description = tagDescriptions.TryGetValue(operation.Tag, out var description) ? description : null,
                };
                dashboard.Tags.Add(tag);
            }

            tag.OperationSlugs.Add(operation.Slug);
        }

        dashboard.Tags.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Falls back to the first meaningful path segment so that documents without tags still
    /// group sensibly, e.g. /api/v1/customers/{id} becomes "customers".
    /// </summary>
    private static string DeriveTagFromPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            if (segment.StartsWith('{'))
            {
                continue;
            }

            if (segment.Equals("api", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip version segments such as v1 or v2.
            if (segment.Length is >= 2 and <= 4 &&
                (segment[0] == 'v' || segment[0] == 'V') &&
                segment.Skip(1).All(char.IsAsciiDigit))
            {
                continue;
            }

            return segment;
        }

        return DashboardConstants.DefaultTag;
    }

    private static List<string> ExtractSecurityKeys(IList<OpenApiSecurityRequirement>? requirements)
    {
        var keys = new List<string>();
        if (requirements is null)
        {
            return keys;
        }

        foreach (var requirement in requirements)
        {
            foreach (var scheme in requirement.Keys)
            {
                var key = scheme.Reference?.Id ?? scheme.Name;
                if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key))
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// Tracks the $ref chain so recursive component schemas terminate instead of expanding
    /// forever, and caps absolute depth for documents that nest deeply without recursing.
    /// </summary>
    private sealed class SchemaContext
    {
        private readonly HashSet<string> _visitedRefs = new(StringComparer.Ordinal);

        public SchemaContext(DashboardDocument dashboard)
        {
            Dashboard = dashboard;
        }

        public DashboardDocument Dashboard { get; }

        public int Depth { get; private set; }

        public bool TryEnter(string? refId)
        {
            if (Depth >= DashboardConstants.MaxSchemaDepth)
            {
                return false;
            }

            if (refId is not null && !_visitedRefs.Add(refId))
            {
                return false;
            }

            Depth++;
            return true;
        }

        public void Leave(string? refId)
        {
            Depth--;
            if (refId is not null)
            {
                _visitedRefs.Remove(refId);
            }
        }
    }

    private static FieldSchema ConvertSchema(OpenApiSchema? schema, SchemaContext context)
    {
        if (schema is null)
        {
            return new FieldSchema();
        }

        var refId = schema.Reference?.Id;

        if (!context.TryEnter(refId))
        {
            return new FieldSchema
            {
                Type = string.IsNullOrEmpty(schema.Type) ? SchemaTypes.Object : schema.Type,
                RefName = refId,
                Truncated = true,
                Description = schema.Description,
            };
        }

        try
        {
            return ConvertSchemaCore(schema, context, refId);
        }
        finally
        {
            context.Leave(refId);
        }
    }

    private static FieldSchema ConvertSchemaCore(OpenApiSchema schema, SchemaContext context, string? refId)
    {
        // allOf is a merge, so it is flattened into a single object rather than offered as
        // a choice; oneOf and anyOf stay as selectable variants.
        var effective = schema;
        if (schema.AllOf is { Count: > 0 })
        {
            effective = MergeAllOf(schema);
        }

        var result = new FieldSchema
        {
            Type = ResolveType(effective),
            Format = effective.Format,
            Title = effective.Title,
            Description = effective.Description ?? schema.Description,
            Nullable = effective.Nullable,
            Pattern = effective.Pattern,
            Minimum = effective.Minimum,
            Maximum = effective.Maximum,
            MinLength = effective.MinLength,
            MaxLength = effective.MaxLength,
            ReadOnly = effective.ReadOnly,
            WriteOnly = effective.WriteOnly,
            RefName = refId,
            Default = RenderExample(effective.Default),
            Example = RenderExample(effective.Example),
        };

        if (effective.Enum is { Count: > 0 })
        {
            foreach (var value in effective.Enum)
            {
                var rendered = RenderExample(value);
                if (rendered is not null)
                {
                    result.Enum.Add(rendered.Trim('"'));
                }
            }
        }

        if (result.Type == SchemaTypes.Array)
        {
            result.Items = ConvertSchema(effective.Items, context);
        }

        if (result.Type == SchemaTypes.Object)
        {
            var required = effective.Required ?? new HashSet<string>();

            if (effective.Properties is not null)
            {
                foreach (var (name, propertySchema) in effective.Properties)
                {
                    result.Properties.Add(new FieldProperty
                    {
                        Name = name,
                        Required = required.Contains(name),
                        Schema = ConvertSchema(propertySchema, context),
                    });
                }
            }

            if (effective.AdditionalPropertiesAllowed && effective.AdditionalProperties is not null)
            {
                result.AdditionalProperties = ConvertSchema(effective.AdditionalProperties, context);
            }
        }

        var alternatives = new List<OpenApiSchema>();
        if (schema.OneOf is { Count: > 0 })
        {
            alternatives.AddRange(schema.OneOf);
        }

        if (schema.AnyOf is { Count: > 0 })
        {
            alternatives.AddRange(schema.AnyOf);
        }

        if (alternatives.Count > 0)
        {
            result.DiscriminatorProperty = schema.Discriminator?.PropertyName;

            for (var i = 0; i < alternatives.Count; i++)
            {
                var alternative = alternatives[i];
                result.Variants.Add(new FieldVariant
                {
                    Name = alternative.Reference?.Id ?? alternative.Title ?? $"Seçenek {i + 1}",
                    Schema = ConvertSchema(alternative, context),
                });
            }

            if (result.Type == SchemaTypes.Unknown)
            {
                result.Type = SchemaTypes.Object;
            }
        }

        return result;
    }

    /// <summary>
    /// Combines the members of an allOf composition into one schema. Only the fields the
    /// dashboard renders are merged; later members win on scalar facets.
    /// </summary>
    private static OpenApiSchema MergeAllOf(OpenApiSchema schema)
    {
        var merged = new OpenApiSchema
        {
            Type = schema.Type,
            Format = schema.Format,
            Title = schema.Title,
            Description = schema.Description,
            Nullable = schema.Nullable,
            Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal),
            Required = new HashSet<string>(StringComparer.Ordinal),
        };

        var members = new List<OpenApiSchema>(schema.AllOf) { schema };

        foreach (var member in members)
        {
            if (!string.IsNullOrEmpty(member.Type))
            {
                merged.Type = member.Type;
            }

            merged.Format ??= member.Format;
            merged.Title ??= member.Title;
            merged.Description ??= member.Description;
            merged.Example ??= member.Example;
            merged.Default ??= member.Default;
            merged.Pattern ??= member.Pattern;
            merged.Minimum ??= member.Minimum;
            merged.Maximum ??= member.Maximum;
            merged.MinLength ??= member.MinLength;
            merged.MaxLength ??= member.MaxLength;
            merged.Items ??= member.Items;

            if (member.Properties is not null)
            {
                foreach (var (name, value) in member.Properties)
                {
                    merged.Properties[name] = value;
                }
            }

            if (member.Required is not null)
            {
                foreach (var name in member.Required)
                {
                    merged.Required.Add(name);
                }
            }

            if (member.AdditionalProperties is not null)
            {
                merged.AdditionalProperties = member.AdditionalProperties;
                merged.AdditionalPropertiesAllowed = member.AdditionalPropertiesAllowed;
            }
        }

        if (string.IsNullOrEmpty(merged.Type) && merged.Properties.Count > 0)
        {
            merged.Type = SchemaTypes.Object;
        }

        return merged;
    }

    private static string ResolveType(OpenApiSchema schema)
    {
        if (!string.IsNullOrWhiteSpace(schema.Type))
        {
            return schema.Type.ToLowerInvariant();
        }

        if (schema.Properties is { Count: > 0 })
        {
            return SchemaTypes.Object;
        }

        if (schema.Items is not null)
        {
            return SchemaTypes.Array;
        }

        if (schema.Enum is { Count: > 0 })
        {
            return SchemaTypes.String;
        }

        return SchemaTypes.Unknown;
    }

    /// <summary>Renders an OpenAPI example value into the JSON text the form shows.</summary>
    private static string? RenderExample(IOpenApiAny? value)
    {
        return value switch
        {
            null => null,
            OpenApiString s => s.Value,
            OpenApiInteger i => i.Value.ToString(),
            OpenApiLong l => l.Value.ToString(),
            OpenApiFloat f => f.Value.ToString("R"),
            OpenApiDouble d => d.Value.ToString("R"),
            OpenApiBoolean b => b.Value ? "true" : "false",
            OpenApiDate date => date.Value.ToString("yyyy-MM-dd"),
            OpenApiDateTime dateTime => dateTime.Value.ToString("O"),
            OpenApiPassword p => p.Value,
            OpenApiNull => null,
            _ => RenderComplexExample(value),
        };
    }

    private static string? RenderComplexExample(IOpenApiAny value)
    {
        try
        {
            using var stream = new MemoryStream();
            var writer = new Microsoft.OpenApi.Writers.OpenApiJsonWriter(new StreamWriter(stream));
            value.Write(writer, Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0);
            writer.Flush();
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (Exception)
        {
            // An unrenderable example must never fail dashboard generation.
            return null;
        }
    }
}
