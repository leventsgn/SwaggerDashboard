using System.Text;
using System.Text.Json;
using SwaggerDashboard.Application.Execution;
using Xunit;

namespace SwaggerDashboard.Tests;

/// <summary>
/// Finding the token in whatever an API's own login endpoint answers with.
/// </summary>
public class TokenResponseReaderTests
{
    private static string Read(string body, string? field = null)
    {
        Assert.True(TokenResponseReader.TryRead(body, field, out var token, out _), body);
        return token!;
    }

    [Theory]
    [InlineData("""{"access_token":"abc"}""")]
    [InlineData("""{"accessToken":"abc"}""")]
    [InlineData("""{"token":"abc"}""")]
    [InlineData("""{"jwt":"abc"}""")]
    [InlineData("""{"authToken":"abc"}""")]
    public void The_usual_field_names_are_recognised(string body)
    {
        Assert.Equal("abc", Read(body));
    }

    [Theory]
    [InlineData("""{"data":{"accessToken":"abc"}}""")]
    [InlineData("""{"result":{"token":"abc"}}""")]
    [InlineData("""{"payload":{"access_token":"abc"}}""")]
    public void A_token_inside_the_usual_wrappers_is_found(string body)
    {
        Assert.Equal("abc", Read(body));
    }

    [Fact]
    public void A_response_that_is_only_the_token_is_accepted()
    {
        // Hand written login endpoints routinely answer with the bare string.
        Assert.Equal("abc", Read("abc"));
        Assert.Equal("abc", Read("\"abc\""));
    }

    [Fact]
    public void A_named_field_wins_over_the_guesses()
    {
        // Naming a field is how a user corrects a wrong guess, so guessing again after being
        // corrected would be worse than failing.
        var body = """{"token":"yanlis","oturum":{"anahtar":"dogru"}}""";

        Assert.Equal("dogru", Read(body, "oturum.anahtar"));
    }

    [Fact]
    public void A_named_field_that_is_not_there_fails_rather_than_falling_back()
    {
        var body = """{"token":"abc"}""";

        Assert.False(TokenResponseReader.TryRead(body, "data.token", out _, out _));
    }

    [Fact]
    public void A_response_with_no_recognisable_token_is_refused()
    {
        // Better than picking the longest string and sending somebody's name as a bearer.
        var body = """{"kullanici":"levent","rol":"admin","sonuc":true}""";

        Assert.False(TokenResponseReader.TryRead(body, null, out _, out _));
    }

    [Fact]
    public void A_body_that_is_not_json_is_refused_rather_than_throwing()
    {
        Assert.False(TokenResponseReader.TryRead("{bozuk json", null, out _, out _));
        Assert.False(TokenResponseReader.TryRead(null, null, out _, out _));
        Assert.False(TokenResponseReader.TryRead("   ", null, out _, out _));
    }

    [Fact]
    public void A_stated_lifetime_is_used()
    {
        Assert.True(TokenResponseReader.TryRead(
            """{"token":"abc","expires_in":3600}""", null, out _, out var lifetime));

        Assert.Equal(TimeSpan.FromHours(1), lifetime);
    }

    [Fact]
    public void A_jwt_expiry_is_used_when_the_response_does_not_state_one()
    {
        // Nothing is trusted from the token beyond how long to reuse it: a forged exp costs
        // at most one extra sign-in.
        var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var token = Jwt($$"""{"exp":{{expires}}}""");

        Assert.True(TokenResponseReader.TryRead(
            $$"""{"token":"{{token}}"}""", null, out _, out var lifetime));

        Assert.InRange(lifetime, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public void An_already_expired_jwt_falls_back_to_the_default_lifetime()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var token = Jwt($$"""{"exp":{{expires}}}""");

        Assert.True(TokenResponseReader.TryRead(
            $$"""{"token":"{{token}}"}""", null, out _, out var lifetime));

        Assert.Equal(TokenResponseReader.DefaultLifetime, lifetime);
    }

    [Fact]
    public void An_unparseable_jwt_does_not_break_the_read()
    {
        Assert.Equal("abc.def", Read("""{"token":"abc.def"}"""));
    }

    private static string Jwt(string payloadJson)
    {
        static string Segment(string text) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("""{"alg":"none"}""")}.{Segment(payloadJson)}.imza";
    }
}
