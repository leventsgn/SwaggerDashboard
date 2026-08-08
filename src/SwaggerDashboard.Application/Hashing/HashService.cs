using System.Security.Cryptography;
using System.Text;

namespace SwaggerDashboard.Application.Hashing;

public interface IHashService
{
    /// <summary>SHA-256 over the canonicalized OpenAPI document.</summary>
    string ComputeSwaggerHash(string swaggerJson);

    /// <summary>SHA-256 over an arbitrary string, lowercase hex.</summary>
    string ComputeSha256(string value);
}

public class HashService : IHashService
{
    public string ComputeSwaggerHash(string swaggerJson) =>
        ComputeSha256(CanonicalJson.Canonicalize(swaggerJson));

    public string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
