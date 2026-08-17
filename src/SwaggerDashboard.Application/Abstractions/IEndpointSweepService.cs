namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// Runs every endpoint of an API in one go and reports how each one answered.
/// </summary>
/// <remarks>
/// Results are streamed rather than returned as a list: a sweep of a large API takes long
/// enough that the user needs to see rows appear, and a cancelled sweep should keep the rows
/// it already produced.
/// </remarks>
public interface IEndpointSweepService
{
    IAsyncEnumerable<SweepResult> RunAsync(SweepRequest request, CancellationToken cancellationToken = default);

    /// <summary>Number of endpoints a sweep with these options would attempt.</summary>
    Task<int> CountAsync(SweepRequest request, CancellationToken cancellationToken = default);
}

public record SweepRequest
{
    public required int ApiDefinitionId { get; init; }

    public string? EnvironmentName { get; init; }

    public string? UserId { get; init; }

    public string? ClientIp { get; init; }

    /// <summary>
    /// Limits the sweep to methods that are not supposed to change anything on the target.
    /// </summary>
    public bool ReadOnlyMethodsOnly { get; init; }

    /// <summary>Whether endpoints marked deprecated in the document take part.</summary>
    public bool IncludeDeprecated { get; init; } = true;
}

public enum SweepOutcome
{
    /// <summary>The target answered with a 2xx status.</summary>
    Success,

    /// <summary>The target answered, but not with a success status.</summary>
    Failed,

    /// <summary>No call was made: no honest request could be generated for this operation.</summary>
    Skipped,
}

public record SweepResult
{
    public required string Slug { get; init; }

    public required string Method { get; init; }

    public required string Path { get; init; }

    public required SweepOutcome Outcome { get; init; }

    public int StatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public long DurationMilliseconds { get; init; }

    public long ContentLength { get; init; }

    /// <summary>Why the call was skipped, or what went wrong.</summary>
    public string? Message { get; init; }
}
