namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// Role names used across the platform.
/// Admin: full management, may register and refresh APIs and read every log.
/// Developer: may execute endpoints and trigger auto provisioning of new swagger URLs.
/// Tester: may execute endpoints against already registered APIs.
/// ReadOnly: may browse dashboards but not execute anything.
/// </summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Developer = "Developer";
    public const string Tester = "Tester";
    public const string ReadOnly = "ReadOnly";

    public static readonly string[] All = [Admin, Developer, Tester, ReadOnly];

    /// <summary>Roles permitted to run endpoints through the proxy.</summary>
    public static readonly string[] CanExecute = [Admin, Developer, Tester];

    /// <summary>Roles permitted to create an API definition by visiting an unknown swagger URL.</summary>
    public static readonly string[] CanProvision = [Admin, Developer];

    public static bool IsKnown(string? role) =>
        role is not null && Array.Exists(All, r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
}
