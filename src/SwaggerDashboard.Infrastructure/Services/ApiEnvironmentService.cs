using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Services;

public class ApiEnvironmentService : IApiEnvironmentService
{
    private const int MaxNameLength = 64;

    /// <summary>One in-flight environment edit per API.</summary>
    /// <remarks>
    /// Every mutation here is a read-modify-write over the whole list: it reads the siblings,
    /// decides which one is the default, and writes them all back. Two of those interleaving
    /// left the API with two defaults, because each one decided against a list taken before
    /// the other had written. The gate makes the sequence atomic within the process; the unique
    /// index and <see cref="EnsureSingleDefaultAsync"/> cover what it cannot see.
    /// </remarks>
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ApiLocks = new();

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

    public Task<EnvironmentResult> AddAsync(
        int apiDefinitionId,
        string name,
        string baseUrl,
        bool isDefault,
        CancellationToken cancellationToken = default) =>
        WithApiLockAsync(apiDefinitionId, async () =>
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

            var saved = await SaveAsync(apiDefinitionId, validation.Name!, cancellationToken);
            if (saved is not null)
            {
                return saved;
            }

            await EnsureSingleDefaultAsync(
                apiDefinitionId, environment.IsDefault ? environment.Id : null, cancellationToken);

            return EnvironmentResult.Ok();
        });

    public async Task<EnvironmentResult> UpdateAsync(
        int environmentId,
        string name,
        string baseUrl,
        bool isDefault,
        CancellationToken cancellationToken = default)
    {
        var apiDefinitionId = await _db.ApiEnvironments
            .Where(e => e.Id == environmentId)
            .Select(e => (int?)e.ApiDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (apiDefinitionId is null)
        {
            return EnvironmentResult.Fail("Ortam bulunamadı.");
        }

        return await WithApiLockAsync(apiDefinitionId.Value, async () =>
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

            var saved = await SaveAsync(environment.ApiDefinitionId, validation.Name!, cancellationToken);
            if (saved is not null)
            {
                return saved;
            }

            await EnsureSingleDefaultAsync(
                environment.ApiDefinitionId, isDefault ? environmentId : null, cancellationToken);

            return EnvironmentResult.Ok();
        });
    }

    public async Task<EnvironmentResult> DeleteAsync(
        int environmentId,
        CancellationToken cancellationToken = default)
    {
        var apiDefinitionId = await _db.ApiEnvironments
            .Where(e => e.Id == environmentId)
            .Select(e => (int?)e.ApiDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (apiDefinitionId is null)
        {
            return EnvironmentResult.Fail("Ortam bulunamadı.");
        }

        return await WithApiLockAsync(apiDefinitionId.Value, async () =>
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

            var saved = await SaveAsync(environment.ApiDefinitionId, environment.Name, cancellationToken);
            if (saved is not null)
            {
                return saved;
            }

            await EnsureSingleDefaultAsync(environment.ApiDefinitionId, null, cancellationToken);

            return EnvironmentResult.Ok();
        });
    }

    private static void ClearOtherDefaults(List<ApiEnvironment> environments, int? keepId)
    {
        foreach (var other in environments.Where(e => e.Id != keepId && e.IsDefault))
        {
            other.IsDefault = false;
        }
    }

    private static async Task<EnvironmentResult> WithApiLockAsync(
        int apiDefinitionId,
        Func<Task<EnvironmentResult>> action)
    {
        var gate = ApiLocks.GetOrAdd(apiDefinitionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();

        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Saves, and turns a failed save into a message instead of an exception.
    /// </summary>
    /// <remarks>
    /// The caller is a Blazor component, so an escaping <see cref="DbUpdateException"/> tears
    /// the circuit down and the admin loses the page rather than seeing why the save was
    /// refused. The most likely cause is the (ApiDefinitionId, Name) unique index catching a
    /// duplicate the in-memory check missed, which is exactly the case worth naming.
    /// </remarks>
    /// <returns>A failure result, or <c>null</c> when the save succeeded.</returns>
    private async Task<EnvironmentResult?> SaveAsync(
        int apiDefinitionId,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException)
        {
            // The tracked graph now disagrees with the database, so this context cannot be
            // reused for the retry-free check below; a fresh read is the only safe thing left.
            _db.ChangeTracker.Clear();

            var duplicate = await _db.ApiEnvironments
                .AsNoTracking()
                .AnyAsync(e => e.ApiDefinitionId == apiDefinitionId && e.Name == name, cancellationToken);

            return EnvironmentResult.Fail(duplicate
                ? $"'{name}' adında bir ortam zaten var."
                : "Ortam kaydedilemedi. Sayfayı yenileyip tekrar deneyin.");
        }
    }

    /// <summary>
    /// Leaves the API with exactly one default environment.
    /// </summary>
    /// <remarks>
    /// The per-process gate cannot see a second instance of the application, and older rows may
    /// already carry two defaults from before it existed. This runs after every mutation and
    /// repairs both cases: it prefers the environment the caller just marked, falls back to
    /// whichever is already flagged, and otherwise promotes the oldest.
    /// </remarks>
    private async Task EnsureSingleDefaultAsync(
        int apiDefinitionId,
        int? preferredId,
        CancellationToken cancellationToken)
    {
        var all = await _db.ApiEnvironments
            .Where(e => e.ApiDefinitionId == apiDefinitionId)
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);

        if (all.Count == 0)
        {
            return;
        }

        var chosen =
            (preferredId is null ? null : all.FirstOrDefault(e => e.Id == preferredId.Value)) ??
            all.FirstOrDefault(e => e.IsDefault) ??
            all[0];

        var changed = false;

        foreach (var environment in all)
        {
            var shouldBeDefault = environment.Id == chosen.Id;

            if (environment.IsDefault != shouldBeDefault)
            {
                environment.IsDefault = shouldBeDefault;
                changed = true;
            }
        }

        if (changed)
        {
            await _db.SaveChangesAsync(cancellationToken);
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
