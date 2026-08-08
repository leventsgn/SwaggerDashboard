using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Application.Abstractions;

namespace SwaggerDashboard.Infrastructure.BackgroundJobs;

/// <summary>
/// Deletes request log rows past their retention window.
/// </summary>
/// <remarks>
/// ApiRequestLogs grows with every proxied call, so without this the table becomes the
/// largest thing in the database and keeps personal data longer than intended.
/// </remarks>
public class LogRetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogRetentionService> _logger;

    public LogRetentionService(IServiceScopeFactory scopeFactory, ILogger<LogRetentionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var logService = scope.ServiceProvider.GetRequiredService<IRequestLogService>();
                var removed = await logService.PurgeExpiredAsync(stoppingToken);

                if (removed > 0)
                {
                    _logger.LogInformation("Purged {Count} expired request log rows", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed purge must not stop the loop; it retries on the next tick.
                _logger.LogError(ex, "Request log purge failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
