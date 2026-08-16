using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Identity;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// The two things the admin screens read: the account list behind sign-in, and the log
/// table they page through.
/// </summary>
public class AdminScreenDataTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SwaggerDashboardDbContext _db;
    private readonly UserService _users;
    private readonly RequestLogService _logs;

    private const int ApiId = 1;

    public AdminScreenDataTests()
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

        _users = new UserService(_db, NullLogger<UserService>.Instance);
        _logs = new RequestLogService(_db, new StaticOptionsMonitor(), NullLogger<RequestLogService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_user_name_taken_in_another_case_is_refused()
    {
        // SQLite compares case sensitively and SQL Server does not, so this used to create a
        // second account on one database and be refused on the other.
        await _users.CreateAsync("levent", "parola-1234", Roles.Developer, "Levent");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _users.CreateAsync("Levent", "parola-5678", Roles.Tester, "Başka biri"));

        Assert.Contains("zaten var", error.Message);
        Assert.Single(await _db.Users.ToListAsync());
    }

    [Fact]
    public async Task Signing_in_works_whatever_case_the_name_is_typed_in()
    {
        await _users.CreateAsync("levent", "parola-1234", Roles.Developer, "Levent");

        var user = await _users.ValidateAsync("LEVENT", "parola-1234");

        Assert.NotNull(user);
        Assert.Equal("levent", user!.UserName);
    }

    [Fact]
    public async Task A_wrong_password_is_still_refused_in_any_case()
    {
        await _users.CreateAsync("levent", "parola-1234", Roles.Developer, "Levent");

        Assert.Null(await _users.ValidateAsync("LEVENT", "yanlış-parola"));
    }

    [Fact]
    public async Task A_log_page_reports_how_many_rows_it_left_behind()
    {
        // The screen used to ask for the newest 200 and show them with nothing to say a row
        // 201 existed, so a busy day looked like a quiet one.
        await AddLogsAsync(250);

        var first = await _logs.GetPageAsync(null, skip: 0, take: 100);

        Assert.Equal(100, first.Rows.Count);
        Assert.Equal(250, first.TotalCount);
        Assert.True(first.HasMore);
    }

    [Fact]
    public async Task Paging_walks_the_whole_log_without_repeating_or_skipping_a_row()
    {
        await AddLogsAsync(250);

        var seen = new List<long>();

        for (var skip = 0; skip < 250; skip += 100)
        {
            var page = await _logs.GetPageAsync(null, skip, take: 100);
            seen.AddRange(page.Rows.Select(r => r.Id));
        }

        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct().Count());

        var last = await _logs.GetPageAsync(null, skip: 200, take: 100);
        Assert.Equal(50, last.Rows.Count);
        Assert.False(last.HasMore);
    }

    private async Task AddLogsAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            _db.ApiRequestLogs.Add(new ApiRequestLog
            {
                ApiDefinitionId = ApiId,
                RequestUrl = $"https://api.company.com/v1/customers/{index}",
                HttpMethod = "GET",
                ResponseStatusCode = 200,
                IsSuccess = true,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index),
            });
        }

        await _db.SaveChangesAsync();
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public SwaggerDashboardOptions CurrentValue { get; } = new();

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}
