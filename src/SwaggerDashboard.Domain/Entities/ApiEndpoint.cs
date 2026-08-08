namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// Flattened endpoint row. The dashboard renders from DashboardJson; this table exists
/// for search, diffing between swagger refreshes and reporting.
/// </summary>
public class ApiEndpoint
{
    public int Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public ApiDefinition? ApiDefinition { get; set; }

    /// <summary>Stable, URL safe identifier used in shareable links.</summary>
    public string Slug { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string HttpMethod { get; set; } = string.Empty;

    public string? OperationId { get; set; }

    public string? Summary { get; set; }

    public string? Description { get; set; }

    public string? Tag { get; set; }

    public string? RequestSchemaJson { get; set; }

    public string? ResponseSchemaJson { get; set; }

    public string? SecuritySchemaJson { get; set; }

    public bool IsDeprecated { get; set; }

    public bool RequiresAuthentication { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
