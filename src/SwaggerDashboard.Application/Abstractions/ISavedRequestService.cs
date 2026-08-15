using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Stores named requests a user wants to run again on an endpoint.
/// </summary>
/// <remarks>
/// Saved requests are personal: every method takes the user id and filters on it, so one
/// user's stored calls are neither listed nor loadable by another. The filter lives here
/// rather than in the screen, because a check the caller has to remember is a check that will
/// eventually be forgotten.
/// </remarks>
public interface ISavedRequestService
{
    Task<IReadOnlyList<SavedRequest>> ListAsync(
        int apiDefinitionId, string endpointSlug, string userId, CancellationToken cancellationToken = default);

    Task<SavedRequest?> GetAsync(int id, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the request under a name, replacing an entry with the same name on the same
    /// endpoint so that saving twice does not leave two rows the user cannot tell apart.
    /// </summary>
    Task<SavedRequestResult> SaveAsync(
        int apiDefinitionId,
        string endpointSlug,
        string userId,
        string name,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, string userId, CancellationToken cancellationToken = default);
}

public record SavedRequestResult(bool Success, string? Error)
{
    public static SavedRequestResult Ok() => new(true, null);

    public static SavedRequestResult Fail(string error) => new(false, error);
}
