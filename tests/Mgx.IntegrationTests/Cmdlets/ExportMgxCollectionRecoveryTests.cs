using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.Engine.Pagination;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The checkpoint reconciliation and query-option retries of Export-MgxCollection, which the
/// engine-level checkpoint tests do not reach.
/// </summary>
[Collection("Pipeline")]
public class ExportMgxCollectionRecoveryTests : IDisposable
{
    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-export-" + Guid.NewGuid().ToString("N"))).FullName;

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

    private static long ItemCount(MgxResult result) =>
        (long)Assert.Single(result.Output).Properties["ItemCount"].Value;

    [Fact]
    public void A_checkpoint_pointed_at_the_output_file_is_refused()
    {
        var path = InWorkDir("both.jsonl");
        using var host = new MgxTestHost(Json(Page("u1")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("CheckpointPath", path));

        Assert.NotNull(result.Terminating);
        Assert.Equal("CheckpointOutputCollision", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void WhatIf_writes_no_file_and_makes_no_request()
    {
        var path = InWorkDir("nothing.jsonl");
        var handler = Json(Page("u1"));
        using var host = new MgxTestHost(handler);

        host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("WhatIf"));

        Assert.False(File.Exists(path));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void An_export_writes_one_json_line_per_item()
    {
        var path = InWorkDir("users.jsonl");
        using var host = new MgxTestHost(Json(Page("u1", "u2", "u3")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("All"));

        Assert.Equal(3, ItemCount(result));
        var lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        Assert.Equal("u1", JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void An_empty_export_says_a_single_entity_needs_the_other_cmdlet()
    {
        var path = InWorkDir("empty.jsonl");
        using var host = new MgxTestHost(Json("""{ "value": [] }"""));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users/u1")
            .AddParameter("OutputFile", path)
            .AddParameter("All"));

        Assert.Equal(0, ItemCount(result));
        Assert.Contains(result.Warnings, w => w.Contains("Invoke-MgxRequest instead"));
    }

    [Fact]
    public void Stopping_at_the_default_page_size_says_so()
    {
        var path = InWorkDir("capped.jsonl");
        using var host = new MgxTestHost(Json(Page("u1", "u2")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("PageSize", 2));

        Assert.Equal(2, ItemCount(result));
        Assert.Contains(result.Warnings, w => w.Contains("default page size"));
    }

    [Fact]
    public void An_endpoint_that_rejects_the_auto_added_count_is_retried_without_it()
    {
        var path = InWorkDir("retry-count.jsonl");
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

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("Filter", "startsWith(displayName,'a')")
            .AddParameter("All"));

        Assert.Equal(1, ItemCount(result));
        Assert.Contains("$count=true", handler.Requests[0].Uri);
        Assert.DoesNotContain("$count=true", handler.Requests[1].Uri);
    }

    [Fact]
    public void An_endpoint_that_rejects_the_page_size_is_retried_without_it()
    {
        var path = InWorkDir("retry-top.jsonl");
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

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("All"));

        Assert.Equal(1, ItemCount(result));
        Assert.Contains("$top=", handler.Requests[0].Uri);
        Assert.DoesNotContain("$top=", handler.Requests[1].Uri);
    }

    [Fact]
    public void A_graph_error_is_written_rather_than_thrown_and_leaves_no_output_file()
    {
        var path = InWorkDir("failed.jsonl");
        using var host = new MgxTestHost(Json(
            """{ "error": { "code": "Request_ResourceNotFound", "message": "no" } }""",
            HttpStatusCode.NotFound));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("All"));

        Assert.Null(result.Terminating);
        Assert.NotEmpty(result.Errors);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_failed_first_page_leaves_an_existing_export_untouched()
    {
        var path = InWorkDir("previous.jsonl");
        File.WriteAllText(path, """{"id":"old"}""" + Environment.NewLine);
        using var host = new MgxTestHost(Json(
            """{ "error": { "code": "Request_ResourceNotFound", "message": "no" } }""",
            HttpStatusCode.NotFound));

        host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("All"));

        Assert.Equal("""{"id":"old"}""", File.ReadAllLines(path)[0]);
    }

    [Fact]
    public void A_checkpoint_whose_output_never_arrived_is_discarded()
    {
        var path = InWorkDir("gone.jsonl");
        var cpPath = InWorkDir("gone.checkpoint");
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=p2",
            ItemsCollected = 5
        }.Save(cpPath);
        using var host = new MgxTestHost(Json(Page("u1")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.Contains(result.Warnings, w => w.Contains("output file is missing"));
        Assert.Equal(1, ItemCount(result));
        Assert.False(File.Exists(cpPath));
    }

    [Fact]
    public void A_checkpoint_naming_a_temp_that_is_gone_starts_over()
    {
        var path = InWorkDir("orphan.jsonl");
        var cpPath = InWorkDir("orphan.checkpoint");
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=p2",
            ItemsCollected = 5,
            TempFile = InWorkDir("orphan.jsonl.deadbeef.tmp"),
            DataLength = 40
        }.Save(cpPath);
        using var host = new MgxTestHost(Json(Page("u1")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.Contains(result.Warnings, w => w.Contains("temp file is missing or incomplete"));
        Assert.Equal(1, ItemCount(result));
    }

    [Fact]
    public void A_checkpoint_naming_a_temp_that_survived_promotes_it_and_resumes()
    {
        var probePath = InWorkDir("probe.jsonl");
        var probeHandler = Json(Page("probe"));
        using (var probeHost = new MgxTestHost(probeHandler))
        {
            probeHost.Run(ps => ps.AddCommand("Export-MgxCollection")
                .AddParameter("Uri", "/users")
                .AddParameter("OutputFile", probePath)
                .AddParameter("All"));
        }

        var path = InWorkDir("resume.jsonl");
        var cpPath = InWorkDir("resume.checkpoint");
        var tempName = "resume.jsonl." + new string('a', 32) + ".tmp";
        var tempPath = InWorkDir(tempName);
        File.WriteAllText(tempPath, """{"id":"u1"}""" + "\n");

        new PaginationCheckpoint
        {
            Resource = probeHandler.Requests[0].Uri,
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=p2",
            ItemsCollected = 1,
            PageItemsAlreadyWritten = 0,
            TempFile = tempName,
            DataLength = new FileInfo(tempPath).Length
        }.Save(cpPath);

        using var host = new MgxTestHost(Json(Page("u2")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.Contains(result.Warnings, w => w.Contains("Recovered 1 items"));
        Assert.Equal(2, ItemCount(result));
        var ids = File.ReadAllLines(path)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetString());
        Assert.Equal(["u1", "u2"], ids);
        Assert.False(File.Exists(cpPath));
    }

    [Fact]
    public void A_completion_marker_checkpoint_is_deleted_rather_than_resumed()
    {
        var path = InWorkDir("done.jsonl");
        var cpPath = InWorkDir("done.checkpoint");
        File.WriteAllText(path, """{"id":"old"}""" + Environment.NewLine);
        new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = null,
            ItemsCollected = 1
        }.Save(cpPath);
        using var host = new MgxTestHost(Json(Page("u1")));

        var result = host.Run(ps => ps.AddCommand("Export-MgxCollection")
            .AddParameter("Uri", "/users")
            .AddParameter("OutputFile", path)
            .AddParameter("CheckpointPath", cpPath)
            .AddParameter("All"));

        Assert.Equal(1, ItemCount(result));
        Assert.False(File.Exists(cpPath));
    }
}
