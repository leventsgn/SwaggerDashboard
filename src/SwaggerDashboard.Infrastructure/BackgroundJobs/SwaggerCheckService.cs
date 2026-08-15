using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;

namespace SwaggerDashboard.Infrastructure.BackgroundJobs;

/// <summary>
/// Re-checks every active API's swagger document on a timer.
/// </summary>
/// <remarks>
/// Without this, a dashboard stays on the document it was built from until somebody presses
/// refresh — so an endpoint removed upstream keeps being offered, and a new one never shows
/// up. The check is the same one the button performs: the hash is compared first and nothing
/// is rebuilt unless the document actually changed.
///
/// Disabled unless <see cref="ProvisioningOptions.AutoRefreshHours"/> is set. A platform
/// should not begin making outbound calls to every registered target on its own.
/// </remarks>
public class SwaggerCheckService : BackgroundService
{
    /// <summary>
    /// Actor written to the audit fields of anything this rebuilds, so a change nobody
    /// remembers making is traceable to the schedule rather than to a person.
    /// </summary>
    public const string Actor = "otomatik-kontrol";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;
    private readonly ILogger<SwaggerCheckService> _logger;

    public SwaggerCheckService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<SwaggerDashboardOptions> options,
        ILogger<SwaggerCheckService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    private ProvisioningOptions Provisioning => _options.CurrentValue.Provisioning;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Provisioning.AutoRefreshHours;

        if (hours <= 0)
        {
            _logger.LogInformation(
                "Scheduled swagger check is off (Provisioning:AutoRefreshHours is not set)");
            return;
        }

        _logger.LogInformation("Scheduled swagger check runs every {Hours} hour(s)", hours);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));

        // The first sweep waits a full interval. Running at startup would put an outbound
        // call to every target into the moment the application is least able to serve
        // requests, and would repeat on every deployment.
        while (await SafeWaitAsync(timer, stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>Checks every active API once. Internal so a test can drive one pass.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        List<int> ids;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var definitions = scope.ServiceProvider.GetRequiredService<IApiDefinitionService>();

            ids = (await definitions.ListAsync(includeInactive: false, cancellationToken))
                .Select(d => d.Id)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled swagger check could not list APIs");
            return;
        }

        var changed = 0;
        var failed = 0;

        foreach (var id in ids)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                // A scope per API: the check is a long running loop and a single scoped
                // DbContext held across all of them would accumulate every entity it touched.
                using var scope = _scopeFactory.CreateScope();
                var refresh = scope.ServiceProvider.GetRequiredService<ISwaggerRefreshService>();

                var result = await refresh.RefreshAsync(id, Actor, cancellationToken);

                if (!result.Success)
                {
                    failed++;
                    _logger.LogWarning(
                        "Scheduled swagger check failed for API {ApiDefinitionId}: {Error}", id, result.Error);
                }
                else if (result.Changed)
                {
                    changed++;

                    // A changed result always carries a diff, but the type allows it to be
                    // absent (a failure has none), so the log does not depend on that.
                    var diff = result.Diff ?? new EndpointDiff();

                    _logger.LogInformation(
                        "API {ApiDefinitionId} swagger changed: +{Added} -{Removed} ~{Modified}",
                        id, diff.Added.Count, diff.Removed.Count, diff.Modified.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One unreachable target must not stop the sweep: the rest are still worth
                // checking, and the next tick retries this one.
                failed++;
                _logger.LogError(ex, "Scheduled swagger check threw for API {ApiDefinitionId}", id);
            }

            await DelayBetweenAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Scheduled swagger check finished: {Total} API, {Changed} changed, {Failed} failed",
            ids.Count, changed, failed);
    }

    private async Task DelayBetweenAsync(CancellationToken cancellationToken)
    {
        var seconds = Provisioning.AutoRefreshDelaySeconds;

        if (seconds <= 0)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; the caller checks the token on the next iteration.
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
