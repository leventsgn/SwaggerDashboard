using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SwaggerDashboard.Infrastructure.Identity;

namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>
/// Re-checks a session cookie against the stored user.
/// </summary>
/// <remarks>
/// A cookie is a claim about who the user was when they signed in. Roles change and accounts
/// are disabled, and both are things an operator reaches for during an incident, so the
/// snapshot has to be re-checked rather than trusted for its full lifetime.
///
/// This covers ordinary HTTP requests. It cannot cover a Blazor circuit that is already open,
/// because no cookie is presented over the established connection — which is why the proxy
/// asks the database for the caller's role at execution time instead of relying on this.
/// </remarks>
public static class SessionRevalidation
{
    /// <summary>
    /// How often the stored user is re-read. Every request would be correct but wasteful;
    /// a minute is short enough that revocation is effectively immediate to a person.
    /// </summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private const string CheckedAtClaim = "revalidated_at";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (!IsDue(principal))
        {
            return;
        }

        var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = principal.FindFirst(ClaimTypes.Role)?.Value;

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserService>();
        var user = int.TryParse(userId, out var id) ? await users.FindByIdAsync(id) : null;

        if (user is null ||
            !user.IsActive ||
            !string.Equals(user.Role, role, StringComparison.Ordinal))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Renewing stamps the check time into the ticket, so the next minute of requests can
        // skip the database.
        context.ShouldRenew = true;
        Stamp(context, principal);
    }

    private static bool IsDue(ClaimsPrincipal principal)
    {
        var stamped = principal.FindFirst(CheckedAtClaim)?.Value;

        return !long.TryParse(stamped, out var ticks) ||
               DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) >= Interval;
    }

    private static void Stamp(CookieValidatePrincipalContext context, ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        foreach (var existing in identity.FindAll(CheckedAtClaim).ToList())
        {
            identity.RemoveClaim(existing);
        }

        identity.AddClaim(new Claim(CheckedAtClaim, DateTimeOffset.UtcNow.UtcTicks.ToString()));
        context.ReplacePrincipal(principal);
    }
}
