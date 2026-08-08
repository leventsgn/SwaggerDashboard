namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Re-downloads a swagger document and rebuilds the dashboard when it actually changed.
/// </summary>
public interface ISwaggerRefreshService
{
    /// <summary>
    /// Downloads the document, compares the canonical hash and rebuilds only on a change.
    /// </summary>
    Task<RefreshResult> RefreshAsync(int apiDefinitionId, string? actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuilds the dashboard regardless of the hash. Used by the "Dashboard Yeniden Oluştur"
    /// action and when a stored document has an outdated schema version.
    /// </summary>
    Task<RefreshResult> RebuildAsync(int apiDefinitionId, string? actor, CancellationToken cancellationToken = default);
}

public record RefreshResult(
    bool Success,
    bool Changed,
    string? Error,
    EndpointDiff? Diff = null)
{
    public static RefreshResult Unchanged() => new(true, false, null, new EndpointDiff());

    public static RefreshResult Updated(EndpointDiff diff) => new(true, true, null, diff);

    public static RefreshResult Fail(string error) => new(false, false, error);
}

/// <summary>What changed between the previous and the new document.</summary>
public class EndpointDiff
{
    public List<string> Added { get; init; } = [];

    public List<string> Removed { get; init; } = [];

    public List<string> Modified { get; init; } = [];

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Modified.Count > 0;
}
