using System.Net;

namespace SwaggerDashboard.Infrastructure.Security;

/// <summary>
/// Name resolution behind an interface so the outbound policy can be tested without
/// depending on real DNS.
/// </summary>
public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

public class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses;
    }
}
