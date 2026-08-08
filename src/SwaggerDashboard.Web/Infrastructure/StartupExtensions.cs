using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Identity;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Web.Infrastructure;

public static class AuthorizationPolicies
{
    public const string AdminOnly = "swagger-dashboard-admin";
    public const string CanExecute = "swagger-dashboard-execute";
}

public static class RateLimitPolicies
{
    public const string Login = "login";
}

public static class StartupExtensions
{
    /// <summary>
    /// Applies the configured database provider.
    /// </summary>
    /// <remarks>
    /// SQL Server is the supported target and owns the migrations. SQLite exists so the
    /// application can be started and exercised without a SQL Server instance; it creates
    /// the schema directly rather than running migrations.
    /// </remarks>
    public static IServiceCollection AddDatabaseProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "SqlServer";
        var connectionString = configuration.GetConnectionString("SwaggerDashboard");

        // AddSwaggerDashboardInfrastructure already registered a SQL Server context; when a
        // different provider is configured the registration is replaced here.
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            var descriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<SwaggerDashboardDbContext>) ||
                            d.ServiceType == typeof(DbContextOptions))
                .ToList();

            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<SwaggerDashboardDbContext>(options =>
                options.UseSqlite(connectionString ?? "Data Source=swagger-dashboard.db"));
        }

        return services;
    }

    /// <summary>
    /// Prepares the schema and makes sure an administrator exists.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<SwaggerDashboardOptions>>().Value;
        ValidateOutboundPolicy(app.Environment, options, app.Logger);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SwaggerDashboardDbContext>();

        if (db.Database.IsRelational() && db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.Database.EnsureCreatedAsync();
        }

        await SeedAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);
    }

    /// <summary>
    /// Refuses to start with a policy that would make the platform an open proxy.
    /// </summary>
    /// <remarks>
    /// The prefixed URL model lets any authorised user name a target host, so an empty allow
    /// list outside development is a misconfiguration rather than a permissive default.
    /// </remarks>
    private static void ValidateOutboundPolicy(
        IWebHostEnvironment environment,
        SwaggerDashboardOptions options,
        ILogger logger)
    {
        if (environment.IsDevelopment())
        {
            if (options.Outbound.AllowAnyHost)
            {
                logger.LogWarning(
                    "Outbound host allow list is disabled. This is only acceptable in development.");
            }

            return;
        }

        if (options.Outbound.AllowAnyHost)
        {
            throw new InvalidOperationException(
                "SwaggerDashboard:Outbound:AllowAnyHost yalnızca Development ortamında kullanılabilir. " +
                "Üretimde AllowedHostSuffixes tanımlayın.");
        }

        if (options.Outbound.AllowPrivateNetworks)
        {
            throw new InvalidOperationException(
                "SwaggerDashboard:Outbound:AllowPrivateNetworks yalnızca Development ortamında kullanılabilir.");
        }

        if (options.Outbound.AllowedHostSuffixes.Count == 0)
        {
            throw new InvalidOperationException(
                "SwaggerDashboard:Outbound:AllowedHostSuffixes boş. Hedef API alan adlarını tanımlayın.");
        }
    }

    private static async Task SeedAdminAsync(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger logger)
    {
        var db = services.GetRequiredService<SwaggerDashboardDbContext>();

        if (await db.Users.AnyAsync())
        {
            return;
        }

        var userName = configuration["SwaggerDashboard:Seed:AdminUserName"] ?? "admin";
        var password = configuration["SwaggerDashboard:Seed:AdminPassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            // A generated password is printed once rather than shipping a known default.
            password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
                .Replace("+", "A").Replace("/", "B").Replace("=", string.Empty);

            logger.LogWarning(
                "Yönetici hesabı oluşturuldu. Kullanıcı: {UserName} Parola: {Password} " +
                "Bu parola yalnızca bir kez gösterilir; giriş yaptıktan sonra değiştirin.",
                userName, password);
        }

        var users = services.GetRequiredService<IUserService>();
        await users.CreateAsync(userName, password, Roles.Admin, "Yönetici");

        logger.LogInformation("Seeded initial administrator {UserName}", userName);
    }
}
