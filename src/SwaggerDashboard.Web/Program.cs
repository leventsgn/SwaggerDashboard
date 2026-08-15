using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure;
using SwaggerDashboard.Infrastructure.Identity;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Web.Components;
using SwaggerDashboard.Web.Infrastructure;

// Container health probe. The aspnet runtime image ships without curl or wget, so the
// application probes its own /health endpoint instead of the image carrying an HTTP client.
if (args.Contains("--healthcheck"))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
        options.DetailedErrors = builder.Environment.IsDevelopment());

builder.Services.AddSwaggerDashboardInfrastructure(builder.Configuration);
builder.Services.AddDatabaseProvider(builder.Configuration);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.Name = "swagger-dashboard-auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthorizationPolicies.AdminOnly, policy => policy.RequireRole(Roles.Admin));
    options.AddPolicy(AuthorizationPolicies.CanExecute, policy => policy.RequireRole(Roles.CanExecute));
});

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ClientInfo>();
builder.Services.AddScoped<CircuitHandler, ClientInfoCircuitHandler>();
builder.Services.AddScoped<CurrentUserAccessor>();
builder.Services.AddAntiforgery();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A bare 429 is a blank page to whoever is typing: the browser posted a form and got a
    // status with no body back, so the sign in screen appears to have silently failed. The
    // login form is sent back instead, with a message saying to wait.
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/account/login"))
        {
            context.HttpContext.Response.Redirect("/login?error=rate");
            return;
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsync(
            "İstek sınırı aşıldı, biraz sonra tekrar deneyin.", cancellationToken);
    };

    // The login endpoint is the one anonymous write path, so it gets its own bucket.
    options.AddFixedWindowLimiter(RateLimitPolicies.Login, limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

// Uploaded files are buffered in memory before being forwarded, so the ceiling is modest.
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 32 * 1024 * 1024);

var hosting = builder.Configuration
    .GetSection(SwaggerDashboardOptions.SectionName)
    .Get<SwaggerDashboardOptions>()?.Hosting ?? new HostingOptions();

if (hosting.BehindReverseProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        // Managed platforms route through addresses that are neither stable nor known in
        // advance, so the default loopback-only trust list would discard the headers and
        // leave the application thinking every request arrived over plain HTTP.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

if (!string.IsNullOrWhiteSpace(hosting.DataProtectionKeyPath))
{
    var keyDirectory = Directory.CreateDirectory(hosting.DataProtectionKeyPath);

    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(keyDirectory)
        .SetApplicationName("SwaggerDashboard");
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
}

// Must run before anything reads the scheme or the caller's address: the HTTPS redirect,
// the secure cookie policy and the client IP written to the audit log all depend on it.
if (hosting.BehindReverseProxy)
{
    app.UseForwardedHeaders();
    app.Logger.LogInformation(
        "Trusting X-Forwarded-* headers. The application must not be reachable except through its proxy.");
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    await next();
});

app.UseStatusCodePagesWithReExecute("/error/{0}");
app.UseHttpsRedirection();
app.UseStaticFiles();

// Routing has to run after the static file middleware, not before it. The dashboard's
// catch-all page route matches every path, and the static file middleware steps aside
// whenever an endpoint has already been selected, so with the default ordering every
// stylesheet and script would be answered by the dashboard page instead of the file.
app.UseRouting();

app.UseMiddleware<ClientInfoMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// After authentication, not before it. An antiforgery token generated for a signed in user
// carries that user's name, and validation compares it against the current user — which is
// still anonymous if this runs first. The effect is that any form rendered while signed in
// fails to post with "the provided antiforgery token was meant for a different claims-based
// user", which is how signing in as a second user produced an exception page.
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAccountEndpoints();
app.MapDownloadEndpoint();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

await app.InitializeDatabaseAsync();

app.Run();

return 0;

/// <summary>Exposed so the integration tests can drive the application host.</summary>
public partial class Program;
