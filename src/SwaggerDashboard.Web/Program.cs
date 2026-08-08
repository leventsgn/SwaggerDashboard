using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Server.Circuits;
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

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
    app.UseHsts();
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
app.UseAntiforgery();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAccountEndpoints();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

await app.InitializeDatabaseAsync();

app.Run();

return 0;

/// <summary>Exposed so the integration tests can drive the application host.</summary>
public partial class Program;
