using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwaggerDashboard.Application.Abstractions;
using SwaggerDashboard.Application.Configuration;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.BackgroundJobs;
using Xunit;

namespace SwaggerDashboard.Tests;

public class SwaggerCheckServiceTests
{
    [Fact]
    public async Task Every_active_api_is_checked_once_per_pass()
    {
        var refresh = new RecordingRefreshService();
        var service = Build(refresh, [1, 2, 3]);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], refresh.Checked);
    }

    [Fact]
    public async Task Inactive_apis_are_not_checked()
    {
        // Listing with includeInactive false is the point: a paused API should not have its
        // target contacted on a timer.
        var definitions = new StubDefinitionService([1, 2]) { InactiveIds = [2] };
        var refresh = new RecordingRefreshService();
        var service = Build(refresh, definitions);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal([1], refresh.Checked);
    }

    [Fact]
    public async Task The_scheduled_check_identifies_itself_as_the_actor()
    {
        // A rebuild nobody remembers asking for should be traceable to the schedule rather
        // than to whoever happened to be signed in.
        var refresh = new RecordingRefreshService();
        var service = Build(refresh, [1]);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(SwaggerCheckService.Actor, Assert.Single(refresh.Actors));
    }

    [Fact]
    public async Task One_unreachable_target_does_not_stop_the_others()
    {
        var refresh = new RecordingRefreshService { ThrowForId = 2 };
        var service = Build(refresh, [1, 2, 3]);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], refresh.Checked);
    }

    [Fact]
    public async Task A_failed_refresh_does_not_stop_the_others()
    {
        var refresh = new RecordingRefreshService { FailForId = 1 };
        var service = Build(refresh, [1, 2]);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal([1, 2], refresh.Checked);
    }

    [Fact]
    public async Task Cancellation_stops_the_pass_where_it_is()
    {
        var refresh = new RecordingRefreshService();
        using var cts = new CancellationTokenSource();
        refresh.OnChecked = _ => cts.Cancel();

        var service = Build(refresh, [1, 2, 3]);

        await service.RunOnceAsync(cts.Token);

        Assert.Single(refresh.Checked);
    }

    private static SwaggerCheckService Build(ISwaggerRefreshService refresh, IEnumerable<int> ids) =>
        Build(refresh, new StubDefinitionService(ids));

    private static SwaggerCheckService Build(
        ISwaggerRefreshService refresh,
        StubDefinitionService definitions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IApiDefinitionService>(definitions);
        services.AddSingleton(refresh);

        var options = new SwaggerDashboardOptions();

        // No pause between APIs; the delay exists to be kind to targets, not to slow tests.
        options.Provisioning.AutoRefreshDelaySeconds = 0;

        return new SwaggerCheckService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor(options),
            NullLogger<SwaggerCheckService>.Instance);
    }

    private sealed class RecordingRefreshService : ISwaggerRefreshService
    {
        public List<int> Checked { get; } = [];

        public List<string?> Actors { get; } = [];

        public int? ThrowForId { get; init; }

        public int? FailForId { get; init; }

        public Action<int>? OnChecked { get; set; }

        public Task<RefreshResult> RefreshAsync(
            int apiDefinitionId, string? actor, CancellationToken cancellationToken = default)
        {
            Checked.Add(apiDefinitionId);
            Actors.Add(actor);
            OnChecked?.Invoke(apiDefinitionId);

            if (apiDefinitionId == ThrowForId)
            {
                throw new HttpRequestException("hedefe ulaşılamadı");
            }

            return Task.FromResult(apiDefinitionId == FailForId
                ? RefreshResult.Fail("doküman okunamadı")
                : RefreshResult.Unchanged());
        }

        public Task<RefreshResult> RebuildAsync(
            int apiDefinitionId, string? actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Scheduled check compares the hash; it never forces a rebuild.");
    }

    private sealed class StubDefinitionService : IApiDefinitionService
    {
        private readonly List<int> _ids;

        public StubDefinitionService(IEnumerable<int> ids) => _ids = [.. ids];

        public IReadOnlyCollection<int> InactiveIds { get; init; } = [];

        public Task<IReadOnlyList<ApiDefinition>> ListAsync(
            bool includeInactive, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ApiDefinition> list = _ids
                .Where(id => includeInactive || !InactiveIds.Contains(id))
                .Select(id => new ApiDefinition { Id = id, Name = $"API {id}" })
                .ToList();

            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<ApiDefinition>> ListVisibleAsync(
            ResolveContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The scheduled check lists every active API, not a user's view.");

        public Task<ResolveResult> ResolveAsync(string routeTail, ResolveContext context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApiDefinition?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SwaggerDashboard.Application.Dashboards.DashboardDocument?> GetDashboardAsync(
            int apiDefinitionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RegistrationResult> RegisterAsync(RegisterApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateMetadataAsync(UpdateApiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SetActiveAsync(int id, bool isActive, string? actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<SwaggerDashboardOptions>
    {
        public StaticOptionsMonitor(SwaggerDashboardOptions value) => CurrentValue = value;

        public SwaggerDashboardOptions CurrentValue { get; }

        public SwaggerDashboardOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<SwaggerDashboardOptions, string?> listener) => null;
    }
}
