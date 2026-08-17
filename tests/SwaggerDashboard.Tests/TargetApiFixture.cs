using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SwaggerDashboard.Tests;

/// <summary>
/// A real HTTP server standing in for a target API, so document discovery, the outbound
/// pipeline and the proxy can be exercised over an actual socket rather than a stub.
/// </summary>
public sealed class TargetApiFixture : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TargetApiFixture(WebApplication app, string baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public string BaseAddress { get; }

    /// <summary>Host and path of the swagger UI page, in the dashboard's route form.</summary>
    public string SwaggerRouteTail => new Uri(BaseAddress + "/swagger/index.html").Authority + "/swagger/index.html";

    public string Authority => new Uri(BaseAddress).Authority;

    /// <summary>
    /// Headers the target saw on the most recent call, copied out of the request.
    /// ASP.NET Core recycles the header dictionary once the response completes, so holding
    /// a reference to it would read back empty.
    /// </summary>
    public Dictionary<string, string> LastRequestHeaders { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? LastRequestBody { get; private set; }

    public string? LastRequestPath { get; private set; }

    public string? LastQueryString { get; private set; }

    public static async Task<TargetApiFixture> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        TargetApiFixture? fixture = null;

        // The swagger UI page is HTML on purpose: the platform must not try to parse it and
        // must fall through to probing the well known document locations instead.
        app.MapGet("/swagger/index.html", () => Results.Content(
            "<!DOCTYPE html><html><body><div id=\"swagger-ui\"></div></body></html>", "text/html"));

        app.MapGet("/swagger/v1/swagger.json", () => Results.Content(Document(), "application/json"));

        app.MapGet("/v1/items/{id}", (string id, HttpContext context) =>
        {
            fixture!.Capture(context, null);
            return Results.Json(new { id, name = "Item " + id, tags = new[] { "a", "b" } });
        });

        app.MapPost("/v1/items", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            fixture!.Capture(context, body);

            return Results.Json(new { created = true }, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/v1/boom", () => Results.Problem("kaboom", statusCode: 500));

        app.MapGet("/v1/page", () => Results.Content("<h1>hello</h1><script>alert(1)</script>", "text/html"));

        app.MapGet("/v1/report", (HttpContext context) =>
        {
            context.Response.Headers.ContentDisposition = "attachment; filename=\"rapor.pdf\"";
            return Results.File(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 1, 2, 3 }, "application/pdf");
        });

        await app.StartAsync();

        var address = app.Urls.First();
        fixture = new TargetApiFixture(app, address.TrimEnd('/'));
        return fixture;
    }

    private void Capture(HttpContext context, string? body)
    {
        LastRequestHeaders = context.Request.Headers
            .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        LastRequestBody = body;
        LastRequestPath = context.Request.Path.Value;
        LastQueryString = context.Request.QueryString.Value;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string Document() => JsonSerializer.Serialize(new
    {
        openapi = "3.0.1",
        info = new { title = "Item API", version = "2.0.0" },
        servers = new[] { new { url = "/v1" } },
        paths = new Dictionary<string, object>
        {
            ["/items/{id}"] = new
            {
                get = new
                {
                    operationId = "getItemById",
                    parameters = new object[]
                    {
                        new { name = "id", @in = "path", required = true, schema = new { type = "string" } },
                        new { name = "expand", @in = "query", schema = new { type = "string" } },
                    },
                    responses = new Dictionary<string, object> { ["200"] = new { description = "ok" } },
                },
            },
            ["/items"] = new
            {
                post = new
                {
                    operationId = "createItem",
                    requestBody = new
                    {
                        required = true,
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    type = "object",
                                    required = new[] { "name" },
                                    properties = new Dictionary<string, object>
                                    {
                                        ["name"] = new { type = "string" },
                                        ["count"] = new { type = "integer" },
                                    },
                                },
                            },
                        },
                    },
                    responses = new Dictionary<string, object> { ["201"] = new { description = "created" } },
                },
            },
            ["/boom"] = new
            {
                get = new
                {
                    operationId = "boom",
                    responses = new Dictionary<string, object> { ["500"] = new { description = "error" } },
                },
            },
            ["/page"] = new
            {
                get = new
                {
                    operationId = "page",
                    responses = new Dictionary<string, object> { ["200"] = new { description = "html" } },
                },
            },
            ["/report"] = new
            {
                get = new
                {
                    operationId = "report",
                    responses = new Dictionary<string, object> { ["200"] = new { description = "pdf" } },
                },
            },
        },
    });
}
