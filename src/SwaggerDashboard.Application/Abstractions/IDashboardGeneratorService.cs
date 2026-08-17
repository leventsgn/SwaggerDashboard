using SwaggerDashboard.Application.Dashboards;

namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Turns an OpenAPI document into the persisted <see cref="DashboardDocument"/>.
/// Runs on registration and on refresh only, never on a dashboard read.
/// </summary>
public interface IDashboardGeneratorService
{
    DashboardGenerationResult Generate(string openApiContent);
}

public record DashboardGenerationResult(
    bool Success,
    DashboardDocument? Document,
    string? Error)
{
    public static DashboardGenerationResult Ok(DashboardDocument document) => new(true, document, null);

    public static DashboardGenerationResult Fail(string error) => new(false, null, error);
}
