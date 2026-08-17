using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Security;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

public class ApiEnvironmentServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SwaggerDashboardDbContext _db;
    private readonly ApiEnvironmentService _service;
    private readonly StubValidator _validator = new();

    private const int ApiId = 1;

    public ApiEnvironmentServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new SwaggerDashboardDbContext(
            new DbContextOptionsBuilder<SwaggerDashboardDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _db.ApiDefinitions.Add(new ApiDefinition
        {
            Id = ApiId,
            Name = "Customer API",
            TargetKey = "key",
            BaseUrl = "https://api.company.com/v1",
            SwaggerUrl = "https://api.company.com/swagger",
            SwaggerUrlNormalized = "https://api.company.com/swagger/v1/swagger.json",
        });
        _db.SaveChanges();

        _service = new ApiEnvironmentService(_db, _validator);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task The_first_environment_becomes_the_default_even_when_not_asked_for()
    {
        // Otherwise the dashboard would open with nothing selected.
        var result = await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: false);

        Assert.True(result.Success, result.Error);
        Assert.True((await _service.ListAsync(ApiId)).Single().IsDefault);
    }

    [Fact]
    public async Task Exactly_one_environment_is_the_default_at_any_time()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);
        await _service.AddAsync(ApiId, "Production", "https://api.company.com/v1", isDefault: true);

        var environments = await _service.ListAsync(ApiId);

        Assert.Single(environments.Where(e => e.IsDefault));
        Assert.Equal("Production", environments.Single(e => e.IsDefault).Name);
    }

    [Fact]
    public async Task Unticking_the_default_promotes_another_rather_than_leaving_none()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);
        await _service.AddAsync(ApiId, "Production", "https://api.company.com/v1", isDefault: false);

        var test = (await _service.ListAsync(ApiId)).Single(e => e.Name == "Test");
        await _service.UpdateAsync(test.Id, "Test", test.BaseUrl, isDefault: false);

        var environments = await _service.ListAsync(ApiId);
        Assert.Single(environments.Where(e => e.IsDefault));
        Assert.Equal("Production", environments.Single(e => e.IsDefault).Name);
    }

    [Fact]
    public async Task Deleting_the_default_hands_the_flag_to_a_survivor()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);
        await _service.AddAsync(ApiId, "Production", "https://api.company.com/v1", isDefault: false);

        var test = (await _service.ListAsync(ApiId)).Single(e => e.Name == "Test");
        Assert.True((await _service.DeleteAsync(test.Id)).Success);

        var remaining = (await _service.ListAsync(ApiId)).Single();
        Assert.Equal("Production", remaining.Name);
        Assert.True(remaining.IsDefault);
    }

    [Fact]
    public async Task The_last_environment_may_be_deleted_and_calls_fall_back_to_the_api()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);
        var only = (await _service.ListAsync(ApiId)).Single();

        Assert.True((await _service.DeleteAsync(only.Id)).Success);
        Assert.Empty(await _service.ListAsync(ApiId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_nameless_environment_is_refused(string name)
    {
        var result = await _service.AddAsync(ApiId, name, "https://test.company.com/v1", isDefault: false);

        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("api.company.com/v1")]
    [InlineData("ftp://api.company.com")]
    [InlineData("bir adres değil")]
    public async Task An_address_the_proxy_could_not_use_is_refused(string baseUrl)
    {
        var result = await _service.AddAsync(ApiId, "Test", baseUrl, isDefault: false);

        Assert.False(result.Success);
        Assert.Contains("adres", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_address_outside_the_allow_list_is_refused_at_save_time()
    {
        // Catching it here rather than at call time turns a confusing runtime failure into a
        // validation message on the screen where the mistake was made.
        _validator.Allowed = false;

        var result = await _service.AddAsync(ApiId, "Test", "https://evil.example/v1", isDefault: false);

        Assert.False(result.Success);
        Assert.Contains("izinli", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_environments_of_one_api_cannot_share_a_name()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);

        var duplicate = await _service.AddAsync(ApiId, "Test", "https://other.company.com/v1", isDefault: false);

        Assert.False(duplicate.Success);
        Assert.Contains("zaten var", duplicate.Error);
    }

    [Fact]
    public async Task Renaming_an_environment_to_its_own_name_is_allowed()
    {
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);
        var test = (await _service.ListAsync(ApiId)).Single();

        var result = await _service.UpdateAsync(test.Id, "Test", "https://test2.company.com/v1", isDefault: true);

        Assert.True(result.Success, result.Error);
        Assert.Equal("https://test2.company.com/v1", (await _service.ListAsync(ApiId)).Single().BaseUrl);
    }

    [Fact]
    public async Task Concurrent_saves_leave_exactly_one_default()
    {
        // Each caller gets its own context, mirroring one scope per request. Both read the
        // sibling list before either wrote, so both used to come out flagged as the default
        // and the admin screen showed two.
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);

        var tasks = Enumerable.Range(0, 4)
            .Select(index => Task.Run(async () =>
            {
                var service = new ApiEnvironmentService(NewContext(), _validator);
                await service.AddAsync(
                    ApiId, $"Env{index}", $"https://env{index}.company.com/v1", isDefault: true);
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        var environments = await _service.ListAsync(ApiId);
        Assert.Equal(5, environments.Count);
        Assert.Single(environments.Where(e => e.IsDefault));
    }

    [Fact]
    public async Task A_duplicate_name_that_slips_past_the_check_comes_back_as_a_message()
    {
        // The check and the save cannot be one atomic step across two application instances,
        // so the unique index is the real arbiter. It used to surface as a DbUpdateException
        // that tore down the circuit: the admin lost the page instead of reading why.
        await _service.AddAsync(ApiId, "Test", "https://test.company.com/v1", isDefault: true);

        var context = NewContext();
        var service = new ApiEnvironmentService(context, _validator);

        // Fires between the duplicate check and the insert, which is exactly the window a
        // second instance writes into.
        context.SavingChanges += (_, _) =>
        {
            using var other = NewContext();

            if (other.ApiEnvironments.Any(e => e.Name == "Production"))
            {
                return;
            }

            other.ApiEnvironments.Add(new ApiEnvironment
            {
                ApiDefinitionId = ApiId,
                Name = "Production",
                BaseUrl = "https://api.company.com/v1",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            other.SaveChanges();
        };

        var result = await service.AddAsync(
            ApiId, "Production", "https://other.company.com/v1", isDefault: false);

        Assert.False(result.Success);
        Assert.Contains("zaten var", result.Error);
    }

    private SwaggerDashboardDbContext NewContext() =>
        new(new DbContextOptionsBuilder<SwaggerDashboardDbContext>().UseSqlite(_connection).Options);

    private sealed class StubValidator : IOutboundUrlValidator
    {
        public bool Allowed { get; set; } = true;

        public bool IsHostAllowed(Uri uri, out string? reason)
        {
            reason = Allowed ? null : $"'{uri.Host}' izinli alan adı listesinde değil.";
            return Allowed;
        }

        public bool IsAllowedAddress(IPAddress address) => Allowed;

        public Task<OutboundValidationResult> ValidateAsync(Uri uri, CancellationToken cancellationToken = default) =>
            Task.FromResult(Allowed
                ? OutboundValidationResult.Allowed()
                : OutboundValidationResult.Denied("engellendi"));
    }
}
