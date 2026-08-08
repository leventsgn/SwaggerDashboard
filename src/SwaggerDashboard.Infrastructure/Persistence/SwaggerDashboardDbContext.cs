using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SwaggerDashboard.Domain.Entities;

namespace SwaggerDashboard.Infrastructure.Persistence;

public class SwaggerDashboardDbContext : DbContext
{
    public SwaggerDashboardDbContext(DbContextOptions<SwaggerDashboardDbContext> options)
        : base(options)
    {
    }

    public DbSet<ApiDefinition> ApiDefinitions => Set<ApiDefinition>();

    public DbSet<ApiEndpoint> ApiEndpoints => Set<ApiEndpoint>();

    public DbSet<ApiUrlAlias> ApiUrlAliases => Set<ApiUrlAlias>();

    public DbSet<ApiRequestLog> ApiRequestLogs => Set<ApiRequestLog>();

    public DbSet<ApiEnvironment> ApiEnvironments => Set<ApiEnvironment>();

    public DbSet<SavedRequest> SavedRequests => Set<SavedRequest>();

    public DbSet<FavoriteEndpoint> FavoriteEndpoints => Set<FavoriteEndpoint>();

    public DbSet<DashboardUser> Users => Set<DashboardUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiDefinition>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.RouteName).HasMaxLength(64);
            entity.Property(e => e.TargetKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.SwaggerUrl).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.SwaggerUrlNormalized).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.SwaggerVersion).HasMaxLength(32);
            entity.Property(e => e.ApiVersion).HasMaxLength(64);
            entity.Property(e => e.SwaggerHash).HasMaxLength(64);
            entity.Property(e => e.CreatedBy).HasMaxLength(128);
            entity.Property(e => e.UpdatedBy).HasMaxLength(128);
            entity.Property(e => e.AllowedRoles).HasMaxLength(256);

            // The target key is the primary lookup for the prefixed URL model, so it has to
            // be unique: two rows for the same document would each get their own dashboard.
            entity.HasIndex(e => e.TargetKey).IsUnique();

            // RouteName is an optional alias, so the uniqueness constraint has to tolerate
            // many rows without one.
            entity.HasIndex(e => e.RouteName)
                .IsUnique()
                .HasFilter("[RouteName] IS NOT NULL");

            entity.HasMany(e => e.Endpoints)
                .WithOne(e => e.ApiDefinition!)
                .HasForeignKey(e => e.ApiDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Aliases)
                .WithOne(e => e.ApiDefinition!)
                .HasForeignKey(e => e.ApiDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Environments)
                .WithOne(e => e.ApiDefinition!)
                .HasForeignKey(e => e.ApiDefinitionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiUrlAlias>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UrlKey).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Url).HasMaxLength(2000).IsRequired();

            // Unique across every API: one address can only ever mean one dashboard.
            entity.HasIndex(e => e.UrlKey).IsUnique();
        });

        modelBuilder.Entity<ApiEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Slug).HasMaxLength(160).IsRequired();
            entity.Property(e => e.Path).HasMaxLength(1000).IsRequired();
            entity.Property(e => e.HttpMethod).HasMaxLength(16).IsRequired();
            entity.Property(e => e.OperationId).HasMaxLength(300);
            entity.Property(e => e.Summary).HasMaxLength(1000);
            entity.Property(e => e.Tag).HasMaxLength(200);

            entity.HasIndex(e => new { e.ApiDefinitionId, e.Slug }).IsUnique();
            entity.HasIndex(e => new { e.ApiDefinitionId, e.Tag });
        });

        modelBuilder.Entity<ApiRequestLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RequestUrl).HasMaxLength(2000).IsRequired();
            entity.Property(e => e.HttpMethod).HasMaxLength(16).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(128);
            entity.Property(e => e.ClientIp).HasMaxLength(64);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasIndex(e => e.CreatedAt);
            entity.HasIndex(e => new { e.ApiDefinitionId, e.CreatedAt });
        });

        modelBuilder.Entity<ApiEnvironment>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(64).IsRequired();
            entity.Property(e => e.BaseUrl).HasMaxLength(2000).IsRequired();
            entity.HasIndex(e => new { e.ApiDefinitionId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<SavedRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EndpointSlug).HasMaxLength(160).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.HasIndex(e => new { e.ApiDefinitionId, e.EndpointSlug, e.UserId });
        });

        modelBuilder.Entity<FavoriteEndpoint>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EndpointSlug).HasMaxLength(160).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(128).IsRequired();
            entity.HasIndex(e => new { e.ApiDefinitionId, e.EndpointSlug, e.UserId }).IsUnique();
        });

        modelBuilder.Entity<DashboardUser>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.UserName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.PasswordHash).HasMaxLength(256).IsRequired();
            entity.Property(e => e.PasswordSalt).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Role).HasMaxLength(32).IsRequired();
            entity.HasIndex(e => e.UserName).IsUnique();
        });

        ApplySqliteDateHandling(modelBuilder);
    }

    /// <summary>
    /// Stores <see cref="DateTimeOffset"/> values as UTC ticks on SQLite.
    /// </summary>
    /// <remarks>
    /// SQLite has no date type, so the provider maps DateTimeOffset to text and cannot
    /// translate a range comparison; the log retention purge fails outright. Ticks compare
    /// and sort correctly as integers. SQL Server, which owns the migrations, is untouched.
    /// </remarks>
    private void ApplySqliteDateHandling(ModelBuilder modelBuilder)
    {
        if (Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        var converter = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));

        var nullableConverter = new ValueConverter<DateTimeOffset?, long?>(
            value => value.HasValue ? value.Value.UtcTicks : null,
            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(converter);
                }
                else if (property.ClrType == typeof(DateTimeOffset?))
                {
                    property.SetValueConverter(nullableConverter);
                }
            }
        }
    }
}
