namespace SwaggerDashboard.Domain.Entities;

public class FavoriteEndpoint
{
    public int Id { get; set; }

    public int ApiDefinitionId { get; set; }

    public string EndpointSlug { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
