using Microsoft.AspNetCore.Components.Server.Circuits;

namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>Per circuit or per request client details used when writing audit rows.</summary>
public class ClientInfo
{
    public string? IpAddress { get; set; }
}

/// <summary>
/// Captures the caller's address once, when the circuit is created.
/// </summary>
/// <remarks>
/// A Blazor Server circuit outlives the HTTP request that started it, so the address has to
/// be copied out while that request is still available; reading HttpContext later returns
/// nothing and the audit log would lose the client IP required by the logging rules.
/// </remarks>
public class ClientInfoCircuitHandler : CircuitHandler
{
    public ClientInfoCircuitHandler(ClientInfo clientInfo, IHttpContextAccessor httpContextAccessor)
    {
        var address = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;

        if (address is not null)
        {
            clientInfo.IpAddress = address.ToString();
        }
    }
}

/// <summary>
/// Fills <see cref="ClientInfo"/> for plain HTTP requests, where the circuit handler does
/// not run at all.
/// </summary>
public class ClientInfoMiddleware
{
    private readonly RequestDelegate _next;

    public ClientInfoMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ClientInfo clientInfo)
    {
        clientInfo.IpAddress ??= context.Connection.RemoteIpAddress?.ToString();
        await _next(context);
    }
}
