namespace SwaggerDashboard.Domain.Entities;

public class ApiEnvironment
{
    public int Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public ApiDefinition? ApiDefinition { get; set; }

    /// <summary>Development, Test, PreProd or Production.</summary>
    public string Name { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
