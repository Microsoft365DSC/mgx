using System.Collections;
using Mgx.E2ETests.Infrastructure;

namespace Mgx.E2ETests;

/// <summary>
/// Full cmdlet-to-HTTP round trips against a WireMock container. Paging tests assert the
/// container's request count as well as the items, because an SSRF-rejected nextLink ends
/// pagination silently and item counts alone would pass while covering only the first page.
/// </summary>
[Trait("Category", "E2E")]
public class InvokeMgxRequestE2ETests(WireMockGraphFixture fixture)
{
    private static void RequiresDocker() =>
        Assert.SkipUnless(WireMockGraphFixture.DockerAvailable,
            $"Requires a Docker daemon able to run Linux containers. {WireMockGraphFixture.StartupError}");

    private MgxCmdletHost NewHost() => new(fixture.Transport, fixture.GraphEndpoint);

    [Fact]
    public async Task All_walks_every_page_of_a_collection()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        var baseUrl = fixture.GraphEndpoint;
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.UsersWithNext($"{baseUrl}/v1.0/users?$skiptoken=p2", "u1", "u2"),
            "$skiptoken", null));
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.UsersWithNext($"{baseUrl}/v1.0/users?$skiptoken=p3", "u3", "u4"),
            "$skiptoken", "p2"));
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.Users("u5"), "$skiptoken", "p3"));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("All"));

        Assert.Null(result.Terminating);
        Assert.Equal(5, result.Output.Count);
        Assert.Equal(3, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task Output_is_a_hashtable_with_the_graph_properties()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users/u1", """{ "id": "u1", "displayName": "One" }"""));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1"));

        var item = Assert.IsType<Hashtable>(Assert.Single(result.Output).BaseObject);
        Assert.Equal("u1", item["id"]);
        Assert.Equal("One", item["displayName"]);
    }

    [Fact]
    public async Task A_nextLink_on_another_host_stops_pagination()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.UsersWithNext("https://evil.example.com/v1.0/users?$skiptoken=p2", "u1", "u2")));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("All"));

        Assert.Equal(2, result.Output.Count);
        Assert.Equal(1, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task Top_stops_before_the_collection_is_exhausted()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        var baseUrl = fixture.GraphEndpoint;
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.UsersWithNext($"{baseUrl}/v1.0/users?$skiptoken=p2", "u1", "u2", "u3"),
            "$skiptoken", null));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Top", 2));

        Assert.Equal(2, result.Output.Count);
    }

    [Fact]
    public async Task A_post_sends_its_body_and_emits_the_created_entity()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Status("POST", "/v1.0/users", 201, """{ "id": "new-1" }"""));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Method", "POST")
            .AddParameter("Body", """{"displayName":"A"}"""));

        var item = Assert.IsType<Hashtable>(Assert.Single(result.Output).BaseObject);
        Assert.Equal("new-1", item["id"]);

        using var journal = await fixture.JournalAsync();
        var body = journal.RootElement.GetProperty("requests")[0]
            .GetProperty("request").GetProperty("body").GetString();
        Assert.Contains("displayName", body);
    }

    [Fact]
    public async Task A_404_surfaces_as_a_non_terminating_error()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Status("GET", "/v1.0/users/missing", 404,
            """{ "error": { "code": "Request_ResourceNotFound", "message": "Not found." } }"""));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/missing"));

        Assert.Empty(result.Output);
        Assert.True(result.Errors.Count > 0 || result.Terminating != null);
    }

    [Fact]
    public async Task A_throttled_request_is_retried_and_then_succeeds()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Step("GET", "/v1.0/users/u1", "throttle", "Started", "ok",
            429, """{ "error": { "code": "TooManyRequests" } }"""));
        await fixture.StubAsync(GraphStubs.Step("GET", "/v1.0/users/u1", "throttle", "ok", null,
            200, """{ "id": "u1" }"""));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1"));

        Assert.Single(result.Output);
        Assert.Equal(2, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task WhatIf_on_a_write_never_reaches_the_network()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Status("DELETE", "/v1.0/users/u1", 204, "{}"));

        using var host = NewHost();
        host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("Method", "DELETE")
            .AddParameter("WhatIf"));

        Assert.Equal(0, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task Every_request_carries_the_mgx_sdk_version_header()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users/u1", """{ "id": "u1" }"""));

        using var host = NewHost();
        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1"));

        using var journal = await fixture.JournalAsync();
        var headers = journal.RootElement.GetProperty("requests")[0]
            .GetProperty("request").GetProperty("headers").ToString();
        Assert.Contains("SdkVersion", headers);
    }
}
