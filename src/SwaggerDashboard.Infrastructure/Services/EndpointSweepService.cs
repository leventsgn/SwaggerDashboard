using System.Runtime.CompilerServices;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Runs every endpoint of an API with generated sample data.
/// </summary>
public class EndpointSweepService : IEndpointSweepService
{
    private static readonly string[] ReadOnlyMethods = ["GET", "HEAD", "OPTIONS"];

    private readonly IApiDefinitionService _definitionService;
    private readonly IApiProxyService _proxyService;

    public EndpointSweepService(IApiDefinitionService definitionService, IApiProxyService proxyService)
    {
        _definitionService = definitionService;
        _proxyService = proxyService;
    }

    public static bool IsReadOnly(string method) =>
        ReadOnlyMethods.Contains(method, StringComparer.OrdinalIgnoreCase);

    public async Task<int> CountAsync(SweepRequest request, CancellationToken cancellationToken = default) =>
        (await SelectOperationsAsync(request, cancellationToken)).Count;

    public async IAsyncEnumerable<SweepResult> RunAsync(
        SweepRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var operations = await SelectOperationsAsync(request, cancellationToken);

        // Calls are made one after another. The proxy runs on a scoped DbContext, so parallel
        // calls would collide on it, and hammering a test environment with every endpoint at
        // once is a good way to get a sweep's failures blamed on the sweep itself.
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sample = RequestComposer.CreateSampleRequest(
                operation,
                request.ApiDefinitionId,
                request.EnvironmentName,
                request.UserId,
                request.ClientIp,
                isBulkRun: true);

            if (sample.Request is null)
            {
                yield return new SweepResult
                {
                    Slug = operation.Slug,
                    Method = operation.Method,
                    Path = operation.Path,
                    Outcome = SweepOutcome.Skipped,
                    Message = sample.Reason,
                };

                continue;
            }

            var response = await _proxyService.ExecuteAsync(sample.Request, cancellationToken);

            yield return new SweepResult
            {
                Slug = operation.Slug,
                Method = operation.Method,
                Path = operation.Path,
                Outcome = response.Success ? SweepOutcome.Success : SweepOutcome.Failed,
                StatusCode = response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                DurationMilliseconds = response.DurationMilliseconds,
                ContentLength = response.ContentLength,
                Message = response.Error,
            };
        }
    }

    private async Task<IReadOnlyList<DashboardOperation>> SelectOperationsAsync(
        SweepRequest request,
        CancellationToken cancellationToken)
    {
        var dashboard = await _definitionService.GetDashboardAsync(request.ApiDefinitionId, cancellationToken);

        if (dashboard is null)
        {
            return [];
        }

        return dashboard.Operations
            .Where(o => !request.ReadOnlyMethodsOnly || IsReadOnly(o.Method))
            .Where(o => request.IncludeDeprecated || !o.Deprecated)
            .ToList();
    }
}
