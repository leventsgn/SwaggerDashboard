using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Security;

namespace SwaggerDashboard.Infrastructure.Http;

/// <summary>
/// Names and configures the HttpClient used for every outbound call.
/// </summary>
public static class OutboundHttpClient
{
    public const string Name = "swagger-dashboard-outbound";

    /// <summary>
    /// Names of headers on a request that carry a credential.
    /// </summary>
    /// <remarks>
    /// An API key travels under whatever header the swagger document names, so the sender
    /// cannot recognise it from a fixed list. The builder marks it here instead, and the
    /// redirect loop drops the marked headers when a hop leaves the original origin.
    /// </remarks>
    public static readonly HttpRequestOptionsKey<IReadOnlyList<string>> CredentialHeaderMarker =
        new("swagger-dashboard-credential-headers");

    /// <summary>
    /// Builds the primary handler. Redirects are followed manually by the callers so that
    /// each hop can be re-validated, and the connect callback re-checks the address the
    /// socket is really dialling, which is what closes the DNS rebinding window between
    /// the policy check and the connection.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<SwaggerDashboardOptions>>();
        var validator = services.GetRequiredService<IOutboundUrlValidator>();

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };

        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var endPoint = context.DnsEndPoint;
            var addresses = IPAddress.TryParse(endPoint.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);

            foreach (var address in addresses)
            {
                if (!validator.IsAllowedAddress(address))
                {
                    throw new OutboundBlockedException(
                        $"'{endPoint.Host}' iç ağdaki bir adrese ({address}) çözümlendiği için bağlantı reddedildi.");
                }
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, endPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        // Invalid certificates are refused; the default validation callback already does
        // this, and it is set explicitly so a future edit cannot loosen it by accident.
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None;

        var timeout = options.CurrentValue.Outbound.TimeoutSeconds;
        handler.ConnectTimeout = TimeSpan.FromSeconds(Math.Min(15, Math.Max(5, timeout)));

        return handler;
    }
}

/// <summary>Raised when the outbound policy blocks a connection.</summary>
public class OutboundBlockedException : Exception
{
    public OutboundBlockedException(string message)
        : base(message)
    {
    }
}
