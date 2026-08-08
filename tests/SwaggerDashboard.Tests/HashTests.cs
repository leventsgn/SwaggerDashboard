using SwaggerDashboard.Application.Hashing;
using Xunit;

namespace SwaggerDashboard.Tests;

public class HashTests
{
    private readonly HashService _hashService = new();

    [Fact]
    public void Reordered_keys_and_whitespace_produce_the_same_hash()
    {
        // This is the property the whole "do not rebuild" design rests on: a target API that
        // serializes its document differently between deployments must not look changed.
        var first = _hashService.ComputeSwaggerHash(SampleDocuments.Minimal);
        var second = _hashService.ComputeSwaggerHash(SampleDocuments.MinimalReordered);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_real_content_change_produces_a_different_hash()
    {
        var first = _hashService.ComputeSwaggerHash(SampleDocuments.Minimal);
        var second = _hashService.ComputeSwaggerHash(SampleDocuments.MinimalPlusOperation);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Canonicalization_sorts_object_members_but_preserves_array_order()
    {
        var canonical = CanonicalJson.Canonicalize("""{"b":1,"a":[3,1,2]}""");

        Assert.Equal("""{"a":[3,1,2],"b":1}""", canonical);
    }

    [Fact]
    public void Canonicalization_keeps_numbers_distinct_rather_than_folding_them()
    {
        var withDecimal = CanonicalJson.Canonicalize("""{"v":1.0}""");
        var withoutDecimal = CanonicalJson.Canonicalize("""{"v":1}""");

        Assert.NotEqual(withDecimal, withoutDecimal);
    }

    [Fact]
    public void The_hash_is_lowercase_hex_of_the_expected_length()
    {
        var hash = _hashService.ComputeSha256("swagger-dashboard");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }
}
