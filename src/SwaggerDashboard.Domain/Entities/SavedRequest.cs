namespace SwaggerDashboard.Domain.Entities;

/// <summary>A named request the user stored for reuse against a given endpoint.</summary>
public class SavedRequest
{
    public int Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public string EndpointSlug { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>Serialized <c>ExecutionRequest</c> payload, without credentials.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
