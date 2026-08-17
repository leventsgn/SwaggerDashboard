using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>
/// Reads the signed in dashboard user for the current circuit or request.
/// </summary>
/// <remarks>
/// Identity comes from the authentication state provider rather than from HttpContext,
/// because inside an established Blazor Server circuit there is no HttpContext to read.
/// </remarks>
public class CurrentUserAccessor
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly ClientInfo _clientInfo;

    public CurrentUserAccessor(AuthenticationStateProvider authenticationStateProvider, ClientInfo clientInfo)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _clientInfo = clientInfo;
    }

    public string? ClientIp => _clientInfo.IpAddress;

    public async Task<CurrentUser> GetAsync()
    {
        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        return FromPrincipal(state.User);
    }

    internal static CurrentUser FromPrincipal(ClaimsPrincipal principal)
    {
        var isAuthenticated = principal.Identity?.IsAuthenticated == true;

        return new CurrentUser(
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            principal.Identity?.Name,
            principal.FindFirst(ClaimTypes.Role)?.Value,
            isAuthenticated);
    }
}

public record CurrentUser(string? UserId, string? UserName, string? Role, bool IsAuthenticated)
{
    public bool IsAdmin => string.Equals(Role, Roles.Admin, StringComparison.OrdinalIgnoreCase);

    public bool CanExecute =>
        Role is not null && Array.Exists(Roles.CanExecute, r => string.Equals(r, Role, StringComparison.OrdinalIgnoreCase));

    public bool CanProvision =>
        Role is not null && Array.Exists(Roles.CanProvision, r => string.Equals(r, Role, StringComparison.OrdinalIgnoreCase));

    public ResolveContext ToResolveContext() => new(UserId, UserName, Role, IsAuthenticated);
}
