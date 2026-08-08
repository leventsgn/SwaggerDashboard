namespace SwaggerDashboard.Web.Infrastructure;

/// <summary>
/// Self contained health probe used by the container health check.
/// </summary>
public static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var url = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://127.0.0.1:8080/health";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"health check failed: {ex.Message}");
            return 1;
        }
    }
}
