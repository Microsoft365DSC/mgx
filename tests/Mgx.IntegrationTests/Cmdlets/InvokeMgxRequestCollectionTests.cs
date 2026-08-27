using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The single-URI collection, checkpoint and write paths of Invoke-MgxRequest.
/// </summary>
[Collection("Pipeline")]
public class InvokeMgxRequestCollectionTests : IDisposable
{
    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-invoke-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string InWorkDir(string name) => Path.Combine(_workDir, name);

    private static string Page(params string[] ids) =>
        $$"""{ "value": [ {{string.Join(",", ids.Select(i => $$"""{ "id": "{{i}}" }"""))}} ] }""";

    private static StubHttpMessageHandler Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    private static StubHttpMessageHandler Error(HttpStatusCode status, string code) =>
        Json($$"""{ "error": { "code": "{{code}}", "message": "nope" } }""", status);

    private static string Id(PSObject output) => (string)((Hashtable)output.BaseObject)["id"]!;

    [Fact]
    public void Raw_emits_the_json_text_rather_than_a_hashtable()
    {
        using var host = new MgxTestHost(Json("""{ "id": "u1" }"""));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("Raw"));

        Assert.Contains("\"id\"", Assert.IsType<string>(Assert.Single(result.Output).BaseObject));
    }

    [Fact]
    public void A_collection_payload_from_an_entity_request_is_unwrapped_and_flagged_as_truncated()
    {
        using var host = new MgxTestHost(Json(
            """{ "value": [ { "id": "u1" } ], "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?p=2" }"""));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Equal("u1", Id(Assert.Single(result.Output)));
        Assert.Contains(result.Warnings, w => w.Contains("Use -All"));
    }

    [Fact]
    public void A_count_variable_is_written_into_the_session()
    {
        using var host = new MgxTestHost(Json("""{ "@odata.count": 7, "value": [ { "id": "u1" } ] }"""));

        var result = host.Run(ps => ps
            .AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("CountVariable", "total")
                .AddParameter("All")
            .AddStatement()
            .AddScript("$total"));

        Assert.Contains(result.Output, o => o.BaseObject is long and 7L);
    }

    [Fact]
    public void A_short_collection_against_a_reported_count_is_warned_about()
    {
        using var host = new MgxTestHost(Json("""{ "@odata.count": 500, "value": [ { "id": "u1" } ] }"""));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("All"));

        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void An_endpoint_that_rejects_the_auto_added_count_is_retried_without_it()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{ "error": { "code": "BadRequest", "message": "$count is not supported" } }""",
                    Encoding.UTF8, "application/json")
            })
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page("u1"), Encoding.UTF8, "application/json")
            });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Filter", "startsWith(displayName,'a')")
            .AddParameter("All"));

        Assert.Equal("u1", Id(Assert.Single(result.Output)));
        Assert.Contains("$count=true", handler.Requests[0].Uri);
        Assert.DoesNotContain("$count=true", handler.Requests[1].Uri);
    }

    [Fact]
    public void An_endpoint_that_rejects_the_page_size_is_retried_without_it()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    """{ "error": { "code": "Request_UnsupportedQuery", "message": "$top not supported" } }""",
                    Encoding.UTF8, "application/json")
            })
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Page("u1"), Encoding.UTF8, "application/json")
            });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("All"));

        Assert.Single(result.Output);
        Assert.Contains("$top=", handler.Requests[0].Uri);
        Assert.DoesNotContain("$top=", handler.Requests[1].Uri);
    }

    [Fact]
    public void A_checkpoint_matching_the_request_resumes_from_its_next_link()
    {
        var cpPath = InWorkDir("resume.checkpoint");
        var probeHandler = Json(Page("probe"));
        using (var probe = new MgxTestHost(probeHandler))
        {
            probe.Run(ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("All"));
        }

        new PaginationCheckpoint
        {
            Resource = probeHandler.Requests[0].Uri,
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=p2",
            ItemsCollected = 3
        }.Save(cpPath);

        var handler = Json(Page("u4"));
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.Equal("u4", Id(Assert.Single(result.Output)));
        Assert.Contains("$skiptoken=p2", handler.Requests[0].Uri);
        Assert.False(File.Exists(cpPath));
    }

    [Fact]
    public void A_checkpoint_for_another_request_is_discarded_rather_than_followed()
    {
        var cpPath = InWorkDir("mismatch.checkpoint");
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/groups",
            NextLink = "https://graph.microsoft.com/v1.0/groups?$skiptoken=p2",
            ItemsCollected = 3
        }.Save(cpPath);

        var handler = Json(Page("u1"));
        using var host = new MgxTestHost(handler);

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.DoesNotContain("groups", handler.Requests[0].Uri);
    }

    [Fact]
    public void A_checkpoint_pointing_at_another_host_is_discarded_rather_than_followed()
    {
        var cpPath = InWorkDir("ssrf.checkpoint");
        var probeHandler = Json(Page("probe"));
        using (var probe = new MgxTestHost(probeHandler))
        {
            probe.Run(ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users")
                .AddParameter("All"));
        }

        new PaginationCheckpoint
        {
            Resource = probeHandler.Requests[0].Uri,
            NextLink = "https://evil.example.com/v1.0/users?$skiptoken=p2",
            ItemsCollected = 3
        }.Save(cpPath);

        var handler = Json(Page("u1"));
        using var host = new MgxTestHost(handler);

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.DoesNotContain("evil.example.com", handler.Requests[0].Uri);
    }

    [Fact]
    public void A_completion_marker_checkpoint_is_deleted_and_the_run_starts_fresh()
    {
        var cpPath = InWorkDir("done.checkpoint");
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = null,
            ItemsCollected = 3
        }.Save(cpPath);
        using var host = new MgxTestHost(Json(Page("u1")));

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.False(File.Exists(cpPath));
    }

    // --- writes ---

    [Fact]
    public void A_delete_that_answers_204_emits_nothing()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("Method", "DELETE")
            .AddParameter("Confirm", false));

        Assert.Empty(result.Output);
        Assert.Equal(HttpMethod.Delete, handler.Requests[0].Method);
    }

    [Fact]
    public void A_hashtable_body_is_serialized_to_json()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{ "id": "new-1" }""", Encoding.UTF8, "application/json")
            };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Method", "POST")
            .AddParameter("Body", new Hashtable { ["displayName"] = "A" })
            .AddParameter("Confirm", false));

        Assert.Contains("displayName", sentBody);
        Assert.Equal("new-1", Id(Assert.Single(result.Output)));
    }

    [Fact]
    public void A_write_that_answers_an_empty_body_emits_nothing()
    {
        var handler = new StubHttpMessageHandler()
            .Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("Method", "PATCH")
            .AddParameter("Body", """{ "department": "Sales" }""")
            .AddParameter("Confirm", false));

        Assert.Empty(result.Output);
        Assert.Null(result.Terminating);
    }

    [Fact]
    public void A_failed_write_is_a_non_terminating_error()
    {
        using var host = new MgxTestHost(Error(HttpStatusCode.Conflict, "Request_BadRequest"));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Method", "POST")
            .AddParameter("Body", "{}")
            .AddParameter("Confirm", false));

        Assert.Null(result.Terminating);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void SkipForbidden_swallows_a_403_on_a_single_request()
    {
        using var host = new MgxTestHost(Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied"));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("SkipForbidden"));

        Assert.Empty(result.Output);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void SkipNotFound_swallows_a_404_on_a_collection_request()
    {
        using var host = new MgxTestHost(Error(HttpStatusCode.NotFound, "Request_ResourceNotFound"));

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("SkipNotFound")
            .AddParameter("All"));

        Assert.Empty(result.Output);
        Assert.Empty(result.Errors);
    }
}
