namespace SwaggerDashboard.Tests;

/// <summary>
/// OpenAPI documents used across the tests.
/// </summary>
public static class SampleDocuments
{
    /// <summary>The full sample shipped in /samples, copied next to the test assembly.</summary>
    public static string CustomerApi() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", "customer-api-swagger.json"));

    /// <summary>A minimal but valid document, for tests that only need one operation.</summary>
    public const string Minimal = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Tiny API", "version": "1.0.0" },
          "servers": [ { "url": "https://api.example.com/v2" } ],
          "paths": {
            "/ping": {
              "get": {
                "operationId": "ping",
                "summary": "Health probe",
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    /// <summary>Same document with reordered keys and different whitespace.</summary>
    public const string MinimalReordered = """
        {
          "paths": {
            "/ping": {
              "get": {
                "responses": { "200": { "description": "ok" } },
                "summary": "Health probe",
                "operationId": "ping"
              }
            }
          },
          "servers": [ { "url": "https://api.example.com/v2" } ],
          "info": { "version": "1.0.0", "title": "Tiny API" },
          "openapi": "3.0.1"
        }
        """;

    /// <summary>The same API with one operation added, used for refresh diffing.</summary>
    public const string MinimalPlusOperation = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Tiny API", "version": "1.1.0" },
          "servers": [ { "url": "https://api.example.com/v2" } ],
          "paths": {
            "/ping": {
              "get": {
                "operationId": "ping",
                "summary": "Health probe",
                "responses": { "200": { "description": "ok" } }
              }
            },
            "/pong": {
              "post": {
                "operationId": "pong",
                "responses": { "202": { "description": "accepted" } }
              }
            }
          }
        }
        """;
}
