using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;

namespace SwaggerDashboard.Infrastructure.Services;

/// <summary>
/// Caps how many new APIs a single user can register per hour.
/// </summary>
/// <remarks>
/// Auto provisioning downloads and parses a document from a host the user names, so without
/// a cap one account could drive an unbounded number of outbound requests.
/// </remarks>
public class ProvisioningRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<SwaggerDashboardOptions> _options;

    public ProvisioningRateLimiter(IOptionsMonitor<SwaggerDashboardOptions> options)
    {
        _options = options;
    }

    public bool TryAcquire(string userId, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;

        var limit = _options.CurrentValue.Provisioning.MaxProvisionsPerUserPerHour;
        if (limit <= 0)
        {
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromHours(1);
        var queue = _attempts.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                retryAfterSeconds = (int)Math.Ceiling((window - (now - queue.Peek())).TotalSeconds);
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
