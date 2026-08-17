namespace SwaggerDashboard.Domain.Entities;

/// <summary>
/// A dashboard user. This is the platform's own identity and is unrelated to the
/// credentials used when calling a target API.
/// </summary>
public class DashboardUser
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>PBKDF2 derived key, base64.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Per user salt, base64.</summary>
    public string PasswordSalt { get; set; } = string.Empty;

    public int PasswordIterations { get; set; }

    public string Role { get; set; } = Roles.ReadOnly;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
