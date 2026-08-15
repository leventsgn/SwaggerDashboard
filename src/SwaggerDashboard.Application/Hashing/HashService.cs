using System.Security.Cryptography;
using System.Text;

namespace SwaggerDashboard.Application.Hashing;

public interface IHashService
{
    /// <summary>SHA-256 over the canonicalized OpenAPI document.</summary>
    string ComputeSwaggerHash(string swaggerDocument);

    /// <summary>SHA-256 over an arbitrary string, lowercase hex.</summary>
    string ComputeSha256(string value);
}

public class HashService : IHashService
{
    /// <summary>
    /// Hashes an OpenAPI document.
    /// </summary>
    /// <remarks>
    /// JSON is canonicalized first, so a target that reorders keys or reformats whitespace
    /// does not look like a changed document. YAML cannot go through that path; it is hashed
    /// from its text with line endings normalized. The cost is that reformatting a YAML
    /// document counts as a change and rebuilds the dashboard once — which is the right
    /// trade against refusing YAML, a format the reader supports and this platform advertises.
    /// </remarks>
    public string ComputeSwaggerHash(string swaggerDocument) =>
        CanonicalJson.TryCanonicalize(swaggerDocument, out var canonical)
            ? ComputeSha256(canonical!)
            : ComputeSha256(swaggerDocument.Replace("\r\n", "\n").Trim());

    public string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
