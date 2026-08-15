using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

public class ApiEnvironmentService : IApiEnvironmentService
{
    private const int MaxNameLength = 64;

    private readonly SwaggerDashboardDbContext _db;
    private readonly IOutboundUrlValidator _validator;

    public ApiEnvironmentService(SwaggerDashboardDbContext db, IOutboundUrlValidator validator)
    {
        _db = db;
        _validator = validator;
    }

    public async Task<IReadOnlyList<ApiEnvironment>> ListAsync(
        int apiDefinitionId,
        CancellationToken cancellationToken = default) =>
        await _db.ApiEnvironments
            .AsNoTracking()
            .Where(e => e.ApiDefinitionId == apiDefinitionId)
            .OrderByDescending(e => e.IsDefault)
            .ThenBy(e => e.Name)
            .ToListAsync(cancellationToken);

    public async Task<EnvironmentResult> AddAsync(
        int apiDefinitionId,
        string name,
        string baseUrl,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateAsync(apiDefinitionId, null, name, baseUrl, cancellationToken);
        if (validation.Error is not null)
        {
            return EnvironmentResult.Fail(validation.Error);
        }

        var existing = await _db.ApiEnvironments
            .Where(e => e.ApiDefinitionId == apiDefinitionId)
            .ToListAsync(cancellationToken);

        var environment = new ApiEnvironment
        {
            ApiDefinitionId = apiDefinitionId,
            Name = validation.Name!,
            BaseUrl = validation.BaseUrl!,

            // The first environment has to be the default, otherwise nothing would be
            // selected when the dashboard opens.
            IsDefault = isDefault || existing.Count == 0,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.ApiEnvironments.Add(environment);

        if (environment.IsDefault)
        {
            ClearOtherDefaults(existing, null);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return EnvironmentResult.Ok();
    }

    public async Task<EnvironmentResult> UpdateAsync(
        int environmentId,
        string name,
        string baseUrl,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var environment = await _db.ApiEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId, cancellationToken);

        if (environment is null)
        {
            return EnvironmentResult.Fail("Ortam bulunamadı.");
        }

        var validation = await ValidateAsync(
            environment.ApiDefinitionId, environmentId, name, baseUrl, cancellationToken);

        if (validation.Error is not null)
        {
            return EnvironmentResult.Fail(validation.Error);
        }

        var siblings = await _db.ApiEnvironments
            .Where(e => e.ApiDefinitionId == environment.ApiDefinitionId)
            .ToListAsync(cancellationToken);

        environment.Name = validation.Name!;
        environment.BaseUrl = validation.BaseUrl!;

        if (isDefault)
        {
            environment.IsDefault = true;
            ClearOtherDefaults(siblings, environmentId);
        }
        else if (environment.IsDefault && siblings.Count > 1)
        {
            // Something always has to be the default, so unticking the current one promotes
            // another rather than leaving the API without a selected environment.
            environment.IsDefault = false;
            var replacement = siblings.First(e => e.Id != environmentId);
            replacement.IsDefault = true;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return EnvironmentResult.Ok();
    }

    public async Task<EnvironmentResult> DeleteAsync(
        int environmentId,
        CancellationToken cancellationToken = default)
    {
        var environment = await _db.ApiEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId, cancellationToken);

        if (environment is null)
        {
            return EnvironmentResult.Fail("Ortam bulunamadı.");
        }

        var siblings = await _db.ApiEnvironments
            .Where(e => e.ApiDefinitionId == environment.ApiDefinitionId && e.Id != environmentId)
            .ToListAsync(cancellationToken);

        _db.ApiEnvironments.Remove(environment);

        // Removing the default would leave the remaining ones unselectable, so the next one
        // takes over. Removing the last environment is allowed: calls then fall back to the
        // API's own base address.
        if (environment.IsDefault && siblings.Count > 0)
        {
            siblings[0].IsDefault = true;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return EnvironmentResult.Ok();
    }

    private static void ClearOtherDefaults(List<ApiEnvironment> environments, int? keepId)
    {
        foreach (var other in environments.Where(e => e.Id != keepId && e.IsDefault))
        {
            other.IsDefault = false;
        }
    }

    /// <summary>
    /// Checks a name and address before they are stored.
    /// </summary>
    /// <remarks>
    /// The outbound policy is applied here as well as at call time. Saving an environment the
    /// proxy will later refuse turns a configuration mistake into a confusing runtime error,
    /// so the same rule is enforced where the mistake is made. Only the parts that need no
    /// name resolution are checked: an environment is often configured before its host exists.
    /// </remarks>
    private async Task<(string? Name, string? BaseUrl, string? Error)> ValidateAsync(
        int apiDefinitionId,
        int? environmentId,
        string name,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        name = name?.Trim() ?? string.Empty;
        baseUrl = baseUrl?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(name))
        {
            return (null, null, "Ortam adı boş olamaz.");
        }

        if (name.Length > MaxNameLength)
        {
            return (null, null, $"Ortam adı en fazla {MaxNameLength} karakter olabilir.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (null, null, "Taban adres http:// veya https:// ile başlayan tam bir adres olmalıdır.");
        }

        if (!_validator.IsHostAllowed(uri, out var reason))
        {
            return (null, null, reason);
        }

        var nameTaken = await _db.ApiEnvironments.AnyAsync(
            e => e.ApiDefinitionId == apiDefinitionId &&
                 e.Name == name &&
                 (environmentId == null || e.Id != environmentId),
            cancellationToken);

        if (nameTaken)
        {
            return (null, null, $"'{name}' adında bir ortam zaten var.");
        }

        return (name, uri.AbsoluteUri.TrimEnd('/'), null);
    }
}
