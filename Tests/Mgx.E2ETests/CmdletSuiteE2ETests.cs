using System.Collections;
using Mgx.E2ETests.Infrastructure;

namespace Mgx.E2ETests;

[Trait("Category", "E2E")]
public class CmdletSuiteE2ETests(WireMockGraphFixture fixture) : IDisposable
{
    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-e2e-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private static void RequiresDocker() =>
        Assert.SkipUnless(WireMockGraphFixture.DockerAvailable,
            $"Requires a Docker daemon able to run Linux containers. {WireMockGraphFixture.StartupError}");

    private MgxCmdletHost NewHost() => new(fixture.Transport, fixture.GraphEndpoint);

    private string InWorkDir(string name) => Path.Combine(_workDir, name);

    [Fact]
    public async Task A_batch_is_chunked_at_twenty_items_per_request()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        await fixture.StubAsync(GraphStubs.Step("POST", "/v1.0/$batch", "chunks", "Started", "second",
            200, GraphStubs.BatchResponses(
                [.. Enumerable.Range(1, 20).Select(i => GraphStubs.BatchItem(i, 200, $$"""{ "id": "u{{i}}" }"""))])));
        await fixture.StubAsync(GraphStubs.Step("POST", "/v1.0/$batch", "chunks", "second", null,
            200, GraphStubs.BatchResponses(
                [.. Enumerable.Range(1, 5).Select(i => GraphStubs.BatchItem(i, 200, $$"""{ "id": "v{{i}}" }"""))])));

        using var host = NewHost();
        var uris = Enumerable.Range(0, 25).Select(i => $"/users/u{i}").ToArray();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Uri", uris));

        Assert.Null(result.Terminating);
        Assert.Equal(25, result.Output.Count);
        Assert.Equal(2, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task A_batch_item_that_fails_does_not_fail_its_neighbours()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        await fixture.StubAsync(GraphStubs.Status("POST", "/v1.0/$batch", 200,
            GraphStubs.BatchResponses(
                GraphStubs.BatchItem(1, 200, """{ "id": "u0" }"""),
                GraphStubs.BatchItem(2, 404, """{ "error": { "code": "Request_ResourceNotFound" } }"""))));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users/u0", "/users/missing" }));

        // A failed item is still emitted with its status so the caller can react per item.
        Assert.Equal(2, result.Output.Count);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task A_first_delta_sync_stores_the_token_and_the_next_run_resumes_from_it()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        var baseUrl = fixture.GraphEndpoint;
        var statePath = InWorkDir("delta.state");

        await fixture.StubAsync(GraphStubs.Get("/v1.0/users/delta",
            $$"""{ "@odata.deltaLink": "{{baseUrl}}/v1.0/users/delta?$deltatoken=t1", "value": [ { "id": "u1" }, { "id": "u2" } ] }""",
            "$deltatoken", null));
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users/delta",
            $$"""{ "@odata.deltaLink": "{{baseUrl}}/v1.0/users/delta?$deltatoken=t2", "value": [ { "id": "u1" } ] }""",
            "$deltatoken", "t1"));

        using var host = NewHost();
        var first = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", statePath));

        Assert.Null(first.Terminating);
        Assert.Equal(2, first.Output.Count);
        Assert.True(File.Exists(statePath));

        var second = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", statePath));

        Assert.Single(second.Output);
    }

    [Fact]
    public async Task An_export_writes_one_json_line_per_item()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        var baseUrl = fixture.GraphEndpoint;
        var outputFile = InWorkDir("users.jsonl");

        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.UsersWithNext($"{baseUrl}/v1.0/users?$skiptoken=p2", "u1", "u2"),
            "$skiptoken", null));
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users",
            GraphStubs.Users("u3"), "$skiptoken", "p2"));

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", outputFile)
            .AddParameter("All"));

        Assert.Null(result.Terminating);
        Assert.Equal(3, File.ReadAllLines(outputFile).Length);
        Assert.Equal(2, await fixture.RequestCountAsync());
    }

    [Fact]
    public async Task A_relation_is_fetched_and_attached_under_the_requested_name()
    {
        RequiresDocker();
        await fixture.ResetAsync();

        await fixture.StubAsync(GraphStubs.Get("/v1.0/groups/g1/members", GraphStubs.Users("m1", "m2")));

        using var host = NewHost();
        var result = host.Run(ps => ps
            .AddCommand("Expand-MgxRelation")
            .AddParameter("InputObject", new Hashtable { ["id"] = "g1" })
            .AddParameter("Uri", "/groups/{id}/members")
            .AddParameter("As", "members"));

        Assert.Null(result.Terminating);
        var group = Assert.IsType<Hashtable>(Assert.Single(result.Output).BaseObject);
        Assert.Equal("g1", group["id"]);
        Assert.Equal(2, ((IList)group["members"]!).Count);
        Assert.Equal(1, await fixture.RequestCountAsync());
    }

    [Fact]
    public void Options_set_through_the_cmdlet_are_read_back_by_Get_MgxOption()
    {
        RequiresDocker();

        using var host = NewHost();
        host.Run(ps => ps.AddCommand("Set-MgxOption").AddParameter("MaxRetryAttempts", 7));
        var result = host.Run(ps => ps.AddCommand("Get-MgxOption"));

        var options = Assert.Single(result.Output);
        Assert.Equal(7, options.Properties["MaxRetryAttempts"].Value);
    }

    [Fact]
    public async Task Telemetry_counts_the_requests_that_actually_went_out()
    {
        RequiresDocker();
        await fixture.ResetAsync();
        await fixture.StubAsync(GraphStubs.Get("/v1.0/users/u1", """{ "id": "u1" }"""));

        using var host = NewHost();
        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1"));
        var result = host.Run(ps => ps.AddCommand("Get-MgxTelemetry"));

        var telemetry = Assert.Single(result.Output);
        Assert.Equal(1L, telemetry.Properties["Requests"].Value);
    }

    [Fact]
    public void Resilience_reports_as_disabled_until_it_is_enabled()
    {
        RequiresDocker();

        using var host = NewHost();
        var result = host.Run(ps => ps.AddCommand("Get-MgxResilience"));

        var state = Assert.Single(result.Output);
        Assert.Equal(false, state.Properties["IsEnabled"].Value);
    }
}
