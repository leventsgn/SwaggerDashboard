using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Infrastructure.Identity;

namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>
/// Sign in and sign out endpoints.
/// </summary>
/// <remarks>
/// These are plain HTTP endpoints rather than interactive component handlers because
/// issuing an authentication cookie has to happen while the response headers can still be
/// written, which is not the case inside an established Blazor Server circuit.
/// </remarks>
public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/login", async (
            HttpContext context,
            [FromForm] string userName,
            [FromForm] string password,
            [FromForm] string? returnUrl,
            IUserService users,
            CancellationToken cancellationToken) =>
        {
            var user = await users.ValidateAsync(userName ?? string.Empty, password ?? string.Empty, cancellationToken);

            if (user is null)
            {
                return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.UserName),
                new(ClaimTypes.Role, user.Role),
            };

            if (!string.IsNullOrWhiteSpace(user.DisplayName))
            {
                claims.Add(new Claim("display_name", user.DisplayName));
            }

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.Redirect(SafeReturnUrl(returnUrl));
        })
        .AllowAnonymous()
        .RequireRateLimiting(RateLimitPolicies.Login);

        // The token is bound as a form field on purpose. .NET validates antiforgery
        // automatically for endpoints that bind a form, and this one used to bind nothing —
        // so any third-party page could sign a visitor out with a cross-site POST. The
        // parameter is unused; binding it is what turns the validation on.
        group.MapPost("/logout", async (
            HttpContext context,
            [FromForm(Name = "__RequestVerificationToken")] string? antiforgeryToken) =>
        {
            _ = antiforgeryToken;

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    /// <summary>
    /// Serves a binary proxy response as a file download.
    /// </summary>
    /// <remarks>
    /// A separate HTTP request rather than the Blazor circuit, because handing multiple
    /// megabytes to the browser as a base64 JS interop argument is not workable. The token
    /// is single use and bound to the session that produced it.
    /// </remarks>
    public static void MapDownloadEndpoint(this WebApplication app)
    {
        app.MapGet("/download/{token}", (
            string token,
            HttpContext context,
            IResponseDownloadStore store) =>
        {
            var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var download = store.Take(token, userId);

            if (download is null)
            {
                return Results.NotFound();
            }

            return Results.File(download.Content, download.ContentType, download.FileName);
        }).RequireAuthorization();
    }

    /// <summary>
    /// Keeps the post-login redirect on this site. Without this the returnUrl parameter
    /// would be an open redirect straight off the login form.
    /// </summary>
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/apis";
        }

        // Only site relative paths are accepted, and "//host" is rejected because the
        // browser reads it as a protocol relative absolute URL.
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\"))
        {
            return "/apis";
        }

        return returnUrl;
    }
}
