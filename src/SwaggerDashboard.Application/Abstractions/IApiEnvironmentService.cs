using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Manages the base addresses an API can be called through: Development, Test, PreProd,
/// Production and anything else the team names.
/// </summary>
public interface IApiEnvironmentService
{
    Task<IReadOnlyList<ApiEnvironment>> ListAsync(int apiDefinitionId, CancellationToken cancellationToken = default);

    Task<EnvironmentResult> AddAsync(
        int apiDefinitionId, string name, string baseUrl, bool isDefault, CancellationToken cancellationToken = default);

    Task<EnvironmentResult> UpdateAsync(
        int environmentId, string name, string baseUrl, bool isDefault, CancellationToken cancellationToken = default);

    Task<EnvironmentResult> DeleteAsync(int environmentId, CancellationToken cancellationToken = default);
}

public record EnvironmentResult(bool Success, string? Error)
{
    public static EnvironmentResult Ok() => new(true, null);

    public static EnvironmentResult Fail(string error) => new(false, error);
}
