using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// NextLinkValidator is the SSRF guard on pagination: a poisoned @odata.nextLink
/// (crafted Graph response or tampered checkpoint) would otherwise send the bearer
/// token to an attacker-controlled host.
/// </summary>
public class NextLinkValidatorTests
{
    private static readonly Uri GraphHost = new("https://graph.microsoft.com/v1.0/users");

    [Fact]
    public void Accepts_same_host_https_link()
    {
        const string next = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc";

        Assert.Equal(next, NextLinkValidator.Validate(next, GraphHost));
    }

    [Fact]
    public void Rejects_different_host()
    {
        Assert.Null(NextLinkValidator.Validate(
            "https://evil.example.com/v1.0/users?$skiptoken=abc", GraphHost));
    }

    [Fact]
    public void Rejects_scheme_downgrade_to_http()
    {
        // Plaintext would leak the bearer token
        Assert.Null(NextLinkValidator.Validate(
            "http://graph.microsoft.com/v1.0/users", GraphHost));
    }

    [Fact]
    public void Rejects_same_host_on_a_different_port()
    {
        Assert.Null(NextLinkValidator.Validate(
            "https://graph.microsoft.com:8443/v1.0/users", GraphHost));
    }

    [Theory]
    [InlineData("not-an-absolute-uri")]
    [InlineData("/v1.0/users?$skiptoken=abc")]
    [InlineData("ftp://graph.microsoft.com/v1.0/users")]
    public void Rejects_malformed_or_non_https_links(string nextLink)
    {
        Assert.Null(NextLinkValidator.Validate(nextLink, GraphHost));
    }

}
