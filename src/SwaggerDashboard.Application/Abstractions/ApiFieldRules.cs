namespace SwaggerDashboard.Application.Abstractions;

/// <summary>
/// The lengths the API definition columns actually hold, and the checks that keep a value
/// from reaching them oversized.
/// </summary>
/// <remarks>
/// SQL Server does not truncate: a value longer than the column throws, and on the register
/// and edit screens that surfaced as a failed save with a database error rather than a
/// message next to the field. SQLite accepts anything, so the mistake only appeared in
/// production. The numbers here mirror the column definitions in the DbContext, and the
/// checks run before anything is written.
/// </remarks>
public static class ApiFieldRules
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int UrlMaxLength = 2000;
    public const int AllowedRolesMaxLength = 256;

    /// <summary>Validates the fields shared by registering and editing an API.</summary>
    /// <returns>An error message, or <c>null</c> when everything fits.</returns>
    public static string? Validate(
        string? name,
        string? description,
        string? swaggerUrl,
        string? baseUrl,
        string? allowedRoles)
    {
        return TooLong(name, NameMaxLength, "API adı")
            ?? TooLong(description, DescriptionMaxLength, "Açıklama")
            ?? TooLong(swaggerUrl, UrlMaxLength, "Swagger adresi")
            ?? TooLong(baseUrl, UrlMaxLength, "Taban adres")
            ?? TooLong(allowedRoles, AllowedRolesMaxLength, "Rol listesi");
    }

    private static string? TooLong(string? value, int maxLength, string label) =>
        value is not null && value.Length > maxLength
            ? $"{label} en fazla {maxLength} karakter olabilir ({value.Length} karakter girildi)."
            : null;
}
