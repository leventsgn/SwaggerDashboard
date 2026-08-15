using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Hashing;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Infrastructure.BackgroundJobs;
using SwaggerDashboard.Infrastructure.Caching;
using SwaggerDashboard.Infrastructure.Http;
using SwaggerDashboard.Infrastructure.Identity;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Security;
using SwaggerDashboard.Infrastructure.Services;

namespace SwaggerDashboard.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSwaggerDashboardInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SwaggerDashboardOptions>(
            configuration.GetSection(SwaggerDashboardOptions.SectionName));

        services.AddDbContext<SwaggerDashboardDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("SwaggerDashboard"),
                sql => sql.EnableRetryOnFailure(3)));

        services.AddMemoryCache();

        services.AddSingleton<IHashService, HashService>();
        services.AddSingleton<IDashboardCache, MemoryDashboardCache>();
        services.AddSingleton<IApiCredentialStore, MemoryApiCredentialStore>();
        services.AddSingleton<IResponseDownloadStore, MemoryResponseDownloadStore>();
        services.AddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddSingleton<IOutboundUrlValidator, OutboundUrlValidator>();
        services.AddSingleton<ProvisioningRateLimiter>();

        services.AddScoped<IDashboardGeneratorService, DashboardGeneratorService>();
        services.AddScoped<IOpenApiDocumentService, OpenApiDocumentService>();
        services.AddScoped<ApiDefinitionService>();
        services.AddScoped<IApiDefinitionService>(sp => sp.GetRequiredService<ApiDefinitionService>());
        services.AddScoped<ISwaggerRefreshService, SwaggerRefreshService>();
        services.AddScoped<IApiProxyService, ApiProxyService>();
        services.AddScoped<IEndpointSweepService, EndpointSweepService>();
        services.AddScoped<IRequestLogService, RequestLogService>();
        services.AddScoped<IUserEndpointService, UserEndpointService>();
        services.AddScoped<IApiEnvironmentService, ApiEnvironmentService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<GuardedHttpSender>();

        services.AddHttpClient(OutboundHttpClient.Name)
            .ConfigurePrimaryHttpMessageHandler(OutboundHttpClient.CreateHandler);

        services.AddHostedService<LogRetentionService>();

        return services;
    }
}
