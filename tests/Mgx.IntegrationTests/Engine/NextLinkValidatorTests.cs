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
    public void Rejects_host_that_only_shares_a_prefix()
    {
        // graph.microsoft.com.evil.example.com must not pass a naive prefix check
        Assert.Null(NextLinkValidator.Validate(
            "https://graph.microsoft.com.evil.example.com/v1.0/users", GraphHost));
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

    [Fact]
    public void Rejects_null_next_link_and_null_expected_host()
    {
        Assert.Null(NextLinkValidator.Validate(null, GraphHost));
        Assert.Null(NextLinkValidator.Validate("https://graph.microsoft.com/v1.0/users", null));
    }

    [Fact]
    public void Enforces_path_prefix_when_supplied()
    {
        // A tampered checkpoint redirecting /users pagination to /me/messages
        // exfiltrates different data on the same host with the same token.
        Assert.Null(NextLinkValidator.Validate(
            "https://graph.microsoft.com/v1.0/me/messages", GraphHost, "/v1.0/users"));

        const string sameResource = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc";
        Assert.Equal(sameResource,
            NextLinkValidator.Validate(sameResource, GraphHost, "/v1.0/users"));
    }

    [Fact]
    public void Path_prefix_comparison_is_case_insensitive()
    {
        const string next = "https://graph.microsoft.com/v1.0/Users?$skiptoken=abc";

        Assert.Equal(next, NextLinkValidator.Validate(next, GraphHost, "/v1.0/users"));
    }
}
