using System.Net;
using System.Text;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The guards Sync-MgxDelta puts in front of a stored delta state, where a wrong answer either
/// loses changes or follows a tampered URL.
/// </summary>
[Collection("Pipeline")]
public class SyncMgxDeltaGuardTests : IDisposable
{
    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-delta-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string InWorkDir(string name) => Path.Combine(_workDir, name);

    private const string Endpoint = "https://graph.microsoft.com";

    private static StubHttpMessageHandler DeltaPage(string deltaLink = Endpoint + "/v1.0/users/delta?$deltatoken=t1") =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{ "value": [ { "id": "u1" } ], "@odata.deltaLink": "{{deltaLink}}" }""",
                Encoding.UTF8, "application/json")
        });

    private static DeltaState State(string resource = "/users/delta",
        string deltaLink = Endpoint + "/v1.0/users/delta?$deltatoken=t0",
        string apiVersion = "v1.0", string endpoint = Endpoint) => new()
        {
            Resource = resource,
            DeltaLink = deltaLink,
            ApiVersion = apiVersion,
            GraphEndpoint = endpoint,
            LastSync = DateTimeOffset.UtcNow
        };

    private static string ErrorId(MgxResult result) =>
        result.Terminating!.FullyQualifiedErrorId.Split(',')[0];

    [Fact]
    public void The_state_file_and_the_output_file_cannot_be_the_same()
    {
        var path = InWorkDir("both.json");
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path)
            .AddParameter("OutputFile", path));

        Assert.Equal("DeltaPathOutputFileCollision", ErrorId(result));
    }

    [Fact]
    public void The_checkpoint_cannot_share_a_path_with_the_state_file()
    {
        var path = InWorkDir("state.json");
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path)
            .AddParameter("CheckpointPath", path));

        Assert.Equal("CheckpointDeltaPathCollision", ErrorId(result));
    }

    [Fact]
    public void The_checkpoint_cannot_share_a_path_with_the_output_file()
    {
        var output = InWorkDir("out.jsonl");
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", InWorkDir("state.json"))
            .AddParameter("OutputFile", output)
            .AddParameter("CheckpointPath", output));

        Assert.Equal("CheckpointOutputCollision", ErrorId(result));
    }

    [Fact]
    public void A_state_built_against_another_api_version_is_refused()
    {
        var path = InWorkDir("state.json");
        State(apiVersion: "beta").Save(path);
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Equal("DeltaApiVersionMismatch", ErrorId(result));
    }

    [Fact]
    public void A_state_built_for_another_resource_is_refused()
    {
        var path = InWorkDir("state.json");
        State(resource: "/groups/delta").Save(path);
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Equal("DeltaResourceMismatch", ErrorId(result));
    }

    [Fact]
    public void A_state_built_against_another_cloud_is_refused()
    {
        var path = InWorkDir("state.json");
        State(endpoint: "https://graph.microsoft.us").Save(path);
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Equal("DeltaEndpointMismatch", ErrorId(result));
    }

    [Fact]
    public void A_delta_link_pointing_at_another_host_is_refused()
    {
        var path = InWorkDir("state.json");
        State(deltaLink: "https://evil.example.com/v1.0/users/delta?$deltatoken=t0").Save(path);
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Equal("DeltaLinkValidationFailed", ErrorId(result));
    }

    [Fact]
    public void A_delta_link_redirected_to_another_resource_is_refused()
    {
        var path = InWorkDir("state.json");
        State(deltaLink: Endpoint + "/v1.0/me/messages/delta?$deltatoken=t0").Save(path);
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Equal("DeltaLinkPathMismatch", ErrorId(result));
    }

    [Fact]
    public void A_matching_state_is_followed_and_the_new_token_replaces_it()
    {
        var path = InWorkDir("state.json");
        State().Save(path);
        var handler = DeltaPage(Endpoint + "/v1.0/users/delta?$deltatoken=t2");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path));

        Assert.Null(result.Terminating);
        Assert.Contains("$deltatoken=t0", handler.Requests[0].Uri);
        Assert.Contains("$deltatoken=t2", DeltaState.Load(path)!.DeltaLink);
    }

    [Fact]
    public void FullSync_drops_the_stored_token_and_enumerates_again()
    {
        var path = InWorkDir("state.json");
        State().Save(path);
        var handler = DeltaPage();
        using var host = new MgxTestHost(handler);

        host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users/delta")
            .AddParameter("DeltaPath", path)
            .AddParameter("FullSync"));

        Assert.DoesNotContain("$deltatoken=t0", handler.Requests[0].Uri);
    }

    [Fact]
    public void An_absolute_uri_is_refused()
    {
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "https://graph.microsoft.com/v1.0/users/delta")
            .AddParameter("DeltaPath", InWorkDir("state.json")));

        Assert.Equal("AbsoluteUriNotAllowed", ErrorId(result));
    }

    [Fact]
    public void A_uri_that_is_not_a_delta_endpoint_warns()
    {
        using var host = new MgxTestHost(DeltaPage());

        var result = host.Run(ps => ps.AddCommand("Sync-MgxDelta")
            .AddParameter("Uri", "/users")
            .AddParameter("DeltaPath", InWorkDir("state.json")));

        Assert.Contains(result.Warnings, w => w.Contains("/delta"));
    }
}
