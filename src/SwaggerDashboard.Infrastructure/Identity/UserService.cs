using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SwaggerDashboard.Domain.Entities;
using SwaggerDashboard.Infrastructure.Persistence;

namespace SwaggerDashboard.Infrastructure.Identity;

public interface IUserService
{
    Task<DashboardUser?> ValidateAsync(string userName, string password, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardUser>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the stored user behind a sign-in.
    /// </summary>
    /// <remarks>
    /// The role and the active flag in a session cookie are a snapshot of the moment the user
    /// signed in. Anything that acts on them — the proxy above all — has to ask the database
    /// what is true now, or a revoked account keeps its old rights until the cookie expires.
    /// </remarks>
    Task<DashboardUser?> FindByIdAsync(int userId, CancellationToken cancellationToken = default);

    Task<DashboardUser> CreateAsync(
        string userName, string password, string role, string? displayName,
        CancellationToken cancellationToken = default);

    Task SetPasswordAsync(int userId, string password, CancellationToken cancellationToken = default);

    Task SetRoleAsync(int userId, string role, CancellationToken cancellationToken = default);

    Task SetActiveAsync(int userId, bool isActive, CancellationToken cancellationToken = default);
}

public class UserService : IUserService
{
    private readonly SwaggerDashboardDbContext _db;
    private readonly ILogger<UserService> _logger;

    public UserService(SwaggerDashboardDbContext db, ILogger<UserService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DashboardUser?> ValidateAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        // Lowered on both sides so a name means the same account on every database. SQL
        // Server's default collation ignores case and SQLite's does not, which let a SQLite
        // deployment hold "admin" and "Admin" as two separate accounts, and made whether
        // "Admin" could sign in depend on which database the platform happened to run on.
        var lowered = userName.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserName.ToLower() == lowered, cancellationToken);

        if (user is null || !user.IsActive)
        {
            // The same failure is returned whether the account is missing, disabled or the
            // password is wrong, so the response does not enumerate accounts.
            _logger.LogInformation("Failed login for {UserName}", userName);
            return null;
        }

        if (!PasswordHasher.Verify(password, user.PasswordHash, user.PasswordSalt, user.PasswordIterations))
        {
            _logger.LogInformation("Failed login for {UserName}", userName);
            return null;
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        return user;
    }

    public async Task<DashboardUser?> FindByIdAsync(int userId, CancellationToken cancellationToken = default) =>
        await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task<IReadOnlyList<DashboardUser>> ListAsync(CancellationToken cancellationToken = default) =>
        await _db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(cancellationToken);

    public async Task<DashboardUser> CreateAsync(
        string userName,
        string password,
        string role,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        userName = userName?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(userName))
        {
            throw new InvalidOperationException("Kullanıcı adı boş olamaz.");
        }

        if (userName.Length > 128)
        {
            throw new InvalidOperationException("Kullanıcı adı en fazla 128 karakter olabilir.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Parola en az 8 karakter olmalıdır.");
        }

        if (!Roles.IsKnown(role))
        {
            throw new InvalidOperationException($"Geçersiz rol: {role}");
        }

        // Same lowered comparison sign-in uses, so an account cannot be created under a
        // spelling that would then sign in as somebody else.
        var lowered = userName.ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.UserName.ToLower() == lowered, cancellationToken))
        {
            throw new InvalidOperationException($"'{userName}' kullanıcı adı zaten var.");
        }

        var (hash, salt, iterations) = PasswordHasher.Hash(password);

        var user = new DashboardUser
        {
            UserName = userName,
            DisplayName = displayName,
            PasswordHash = hash,
            PasswordSalt = salt,
            PasswordIterations = iterations,
            Role = role,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        return user;
    }

    public async Task SetPasswordAsync(int userId, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new InvalidOperationException("Parola en az 8 karakter olmalıdır.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"Kullanıcı bulunamadı: {userId}");

        var (hash, salt, iterations) = PasswordHasher.Hash(password);
        user.PasswordHash = hash;
        user.PasswordSalt = salt;
        user.PasswordIterations = iterations;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRoleAsync(int userId, string role, CancellationToken cancellationToken = default)
    {
        if (!Roles.IsKnown(role))
        {
            throw new InvalidOperationException($"Geçersiz rol: {role}");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"Kullanıcı bulunamadı: {userId}");

        user.Role = role;
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetActiveAsync(int userId, bool isActive, CancellationToken cancellationToken = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"Kullanıcı bulunamadı: {userId}");

        user.IsActive = isActive;
        await _db.SaveChangesAsync(cancellationToken);
    }
}
