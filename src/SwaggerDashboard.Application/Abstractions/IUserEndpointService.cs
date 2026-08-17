namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Per user endpoint state for one API: which endpoints are starred and which were used most
/// recently.
/// </summary>
public interface IUserEndpointService
{
    Task<IReadOnlyCollection<string>> GetFavoriteSlugsAsync(
        int apiDefinitionId, string userId, CancellationToken cancellationToken = default);

    /// <summary>Stars or unstars an endpoint and returns the resulting state.</summary>
    Task<bool> ToggleFavoriteAsync(
        int apiDefinitionId, string endpointSlug, string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The endpoints this user called most recently, newest first.
    /// </summary>
    /// <remarks>
    /// Derived from the request log rather than a separate table: every proxied call is
    /// already recorded there with the user and the endpoint, so a second store would only
    /// be another thing to keep in step. The trade-off is that the list reaches back exactly
    /// as far as the configured log retention.
    /// </remarks>
    Task<IReadOnlyList<string>> GetRecentSlugsAsync(
        int apiDefinitionId, string userId, int take, CancellationToken cancellationToken = default);
}
