using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SwaggerDashboard.Application.Dashboards;
using SwaggerDashboard.Application.Execution;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;
using SwaggerDashboard.Infrastructure.Services;
using Xunit;

namespace SwaggerDashboard.Tests;

public class SavedRequestServiceTests : IDisposable
{
    private const int ApiId = 1;
    private const string Slug = "get-customers";

    private readonly SqliteConnection _connection;
    private readonly SwaggerDashboardDbContext _db;
    private readonly SavedRequestService _service;

    public SavedRequestServiceTests()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        _db = new SwaggerDashboardDbContext(
            new DbContextOptionsBuilder<SwaggerDashboardDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _service = new SavedRequestService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<SwaggerDashboard.Application.Abstractions.SavedRequestResult> Save(
        string name, string user = "1", string payload = "{}") =>
        _service.SaveAsync(ApiId, Slug, user, name, payload);

    [Fact]
    public async Task A_saved_request_is_listed_for_its_owner()
    {
        Assert.True((await Save("404 senaryosu")).Success);

        var saved = Assert.Single(await _service.ListAsync(ApiId, Slug, "1"));
        Assert.Equal("404 senaryosu", saved.Name);
    }

    [Fact]
    public async Task Another_user_can_neither_see_nor_load_it()
    {
        // Saved requests carry whatever the user typed, so they are personal by default.
        await Save("Benim isteğim", user: "1", payload: """{"Body":"{\"secretish\":\"1\"}"}""");
        var mine = (await _service.ListAsync(ApiId, Slug, "1")).Single();

        Assert.Empty(await _service.ListAsync(ApiId, Slug, "2"));
        Assert.Null(await _service.GetAsync(mine.Id, "2"));
        Assert.False(await _service.DeleteAsync(mine.Id, "2"));

        // And it is still there afterwards.
        Assert.NotNull(await _service.GetAsync(mine.Id, "1"));
    }

    [Fact]
    public async Task Saving_the_same_name_twice_replaces_rather_than_duplicates()
    {
        await Save("Aynı ad", payload: """{"Version":1,"Body":"ilk"}""");
        await Save("Aynı ad", payload: """{"Version":1,"Body":"ikinci"}""");

        var saved = Assert.Single(await _service.ListAsync(ApiId, Slug, "1"));
        Assert.Contains("ikinci", saved.PayloadJson);
    }

    [Fact]
    public async Task The_same_name_on_a_different_endpoint_is_a_different_request()
    {
        await Save("Standart");
        await _service.SaveAsync(ApiId, "post-customers", "1", "Standart", "{}");

        Assert.Single(await _service.ListAsync(ApiId, Slug, "1"));
        Assert.Single(await _service.ListAsync(ApiId, "post-customers", "1"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_nameless_request_is_refused(string name)
    {
        Assert.False((await Save(name)).Success);
    }

    [Fact]
    public async Task An_oversized_payload_is_refused_rather_than_truncated()
    {
        // Silently storing half a body would produce a saved request that runs differently
        // from the one the user saved.
        var result = await Save("Kocaman", payload: new string('x', 200 * 1024));

        Assert.False(result.Success);
        Assert.Contains("büyük", result.Error);
    }

    [Fact]
    public async Task There_is_a_ceiling_on_how_many_one_endpoint_holds()
    {
        for (var i = 0; i < 50; i++)
        {
            Assert.True((await Save($"istek-{i}")).Success);
        }

        var overflow = await Save("bir fazlası");

        Assert.False(overflow.Success);
        Assert.Contains("en fazla", overflow.Error);

        // The limit does not block updating one that already exists.
        Assert.True((await Save("istek-0", payload: """{"Version":1}""")).Success);
    }

    [Fact]
    public async Task Deleting_removes_it_from_the_list()
    {
        await Save("Gidecek");
        var saved = (await _service.ListAsync(ApiId, Slug, "1")).Single();

        Assert.True(await _service.DeleteAsync(saved.Id, "1"));
        Assert.Empty(await _service.ListAsync(ApiId, Slug, "1"));
    }

    [Fact]
    public void A_payload_survives_a_round_trip_through_storage()
    {
        var operation = new DashboardOperation
        {
            Slug = Slug,
            Method = "GET",
            Path = "/customers",
            Parameters =
            [
                new DashboardParameter
                {
                    Name = "status",
                    In = ParameterLocations.Query,
                    Schema = new FieldSchema { Type = SchemaTypes.String },
                },
                new DashboardParameter
                {
                    Name = "tags",
                    In = ParameterLocations.Query,
                    Schema = new FieldSchema
                    {
                        Type = SchemaTypes.Array,
                        Items = new FieldSchema { Type = SchemaTypes.String },
                    },
                },
            ],
        };

        var nodes = RequestComposer.CreateParameterNodes(operation);
        nodes["query:status"].Value = "aktif";
        nodes["query:status"].Included = true;
        nodes["query:tags"].AddItem();
        nodes["query:tags"].Items[0].Value = "öncelikli";

        var json = RequestComposer
            .CapturePayload(nodes, "application/json", """{"ad":"Ada Yılmaz"}""")
            .ToJson();

        // Turkish text stays readable in the stored row rather than becoming \u escapes.
        Assert.Contains("öncelikli", json);

        var restored = SavedRequestPayload.FromJson(json);
        Assert.NotNull(restored);

        var target = RequestComposer.CreateParameterNodes(operation);
        RequestComposer.ApplyPayload(restored!, target, null);

        Assert.Equal("aktif", target["query:status"].Value);
        Assert.Equal("öncelikli", Assert.Single(target["query:tags"].Items).Value);
        Assert.Equal("application/json", restored!.ContentType);
    }

    [Fact]
    public void An_empty_optional_parameter_is_not_stored_as_an_empty_value()
    {
        // An absent query parameter and an empty one are different calls.
        var operation = new DashboardOperation
        {
            Slug = Slug,
            Method = "GET",
            Path = "/customers",
            Parameters =
            [
                new DashboardParameter
                {
                    Name = "status",
                    In = ParameterLocations.Query,
                    Schema = new FieldSchema { Type = SchemaTypes.String },
                },
            ],
        };

        var payload = RequestComposer.CapturePayload(
            RequestComposer.CreateParameterNodes(operation), "application/json", null);

        Assert.Empty(payload.Parameters);
    }

    [Fact]
    public void A_payload_written_by_a_newer_version_is_rejected_instead_of_half_read()
    {
        Assert.Null(SavedRequestPayload.FromJson("""{"Version":99,"Parameters":{}}"""));
        Assert.Null(SavedRequestPayload.FromJson("bu json değil"));
        Assert.Null(SavedRequestPayload.FromJson(null));
    }
}
