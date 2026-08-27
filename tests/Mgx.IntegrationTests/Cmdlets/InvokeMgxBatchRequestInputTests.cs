using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// How Invoke-MgxBatchRequest reads its pipeline input and what it records for the items that
/// failed or never went out.
/// </summary>
[Collection("Pipeline")]
public class InvokeMgxBatchRequestInputTests : IDisposable
{
    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-batch-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string InWorkDir(string name) => Path.Combine(_workDir, name);

    private static string Responses(params string[] items) =>
        $$"""{ "responses": [ {{string.Join(",", items)}} ] }""";

    private static string Item(int id, int status, string body = """{ "ok": true }""") =>
        $$"""{ "id": "{{id}}", "status": {{status}}, "body": {{body}} }""";

    private static StubHttpMessageHandler Batch(string body) =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

    private static Hashtable Row(PSObject output) => (Hashtable)output.BaseObject;

    [Fact]
    public void A_string_url_uses_the_shared_method_and_body()
    {
        string? sent = null;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responses(Item(1, 201)), Encoding.UTF8, "application/json")
            };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Method", "POST")
                .AddParameter("Body", """{ "displayName": "A" }""")
                .AddParameter("Confirm", false),
            new[] { "/users" });

        Assert.Equal("POST", (string)Row(Assert.Single(result.Output))["Method"]!);
        Assert.Contains("displayName", sent);
    }

    [Fact]
    public void A_per_item_method_overrides_the_shared_one()
    {
        using var host = new MgxTestHost(Batch(Responses(Item(1, 204, "null"))));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            new[] { new Hashtable { ["Url"] = "/users/u1", ["Method"] = "delete" } });

        Assert.Equal("DELETE", (string)Row(Assert.Single(result.Output))["Method"]!);
    }

    [Fact]
    public void An_unknown_method_is_skipped_with_a_warning()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            new[] { new Hashtable { ["Url"] = "/users/u1", ["Method"] = "TRACE" } });

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(result.Warnings, w => w.Contains("invalid HTTP method"));
    }

    [Fact]
    public void Input_that_is_neither_a_url_nor_a_request_is_skipped_with_a_warning()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            new object[] { 42 });

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(result.Warnings, w => w.Contains("unrecognized pipeline input"));
    }

    [Fact]
    public void An_item_whose_body_is_not_json_fails_on_its_own()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Method", "POST")
                .AddParameter("Confirm", false),
            new[]
            {
                new Hashtable { ["Url"] = "/users/a", ["Body"] = "{ not json" },
                new Hashtable { ["Url"] = "/users/b", ["Body"] = """{ "ok": 1 }""" }
            });

        Assert.Single(result.Output);
        Assert.Contains(result.Errors,
            e => e.FullyQualifiedErrorId.StartsWith("InvalidBatchItemBody", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatIf_names_the_methods_it_would_send_and_sends_none()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("WhatIf"),
            new[]
            {
                new Hashtable { ["Url"] = "/users/a", ["Method"] = "PATCH" },
                new Hashtable { ["Url"] = "/users/b", ["Method"] = "DELETE" }
            });

        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void A_batch_of_reads_is_gated_by_WhatIf_as_well()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("WhatIf"),
            new[] { "/users/a" });

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void An_absolute_url_of_another_api_version_is_warned_about_and_rewritten()
    {
        string? sent = null;
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            sent = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responses(Item(1, 200)), Encoding.UTF8, "application/json")
            };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            new[] { "https://graph.microsoft.com/beta/users/u1" });

        Assert.Contains(result.Warnings, w => w.Contains("-ApiVersion"));
        Assert.Contains("\"/users/u1\"", sent);
    }

    [Fact]
    public void A_failed_item_is_written_to_the_dead_letter_file_with_its_secrets_removed()
    {
        var path = InWorkDir("dead.jsonl");
        using var host = new MgxTestHost(Batch(Responses(
            Item(1, 400, """{ "error": { "code": "Request_BadRequest", "message": "bad" } }"""))));

        host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Method", "POST")
                .AddParameter("DeadLetterPath", path)
                .AddParameter("Confirm", false),
            new[]
            {
                new Hashtable
                {
                    ["Url"] = "/users",
                    ["Body"] = new Hashtable
                    {
                        ["displayName"] = "A",
                        ["passwordProfile"] = new Hashtable { ["password"] = "hunter2" }
                    }
                }
            });

        var line = JsonDocument.Parse(File.ReadAllLines(path)[0]).RootElement;
        Assert.Equal(400, line.GetProperty("Status").GetInt32());
        Assert.Equal("***REDACTED***", line.GetProperty("Body").GetProperty("passwordProfile").GetString());
        Assert.Equal("A", line.GetProperty("Body").GetProperty("displayName").GetString());
        Assert.Contains("Request_BadRequest", line.GetProperty("Error").GetString());
    }

    [Fact]
    public void A_successful_batch_writes_no_dead_letter_file_content()
    {
        var path = InWorkDir("clean.jsonl");
        using var host = new MgxTestHost(Batch(Responses(Item(1, 200))));

        host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("DeadLetterPath", path)
                .AddParameter("Confirm", false),
            new[] { "/users/u1" });

        Assert.True(!File.Exists(path) || File.ReadAllText(path).Length == 0);
    }

    [Fact]
    public void A_failed_item_carries_the_graph_error_into_the_error_stream()
    {
        using var host = new MgxTestHost(Batch(Responses(
            Item(1, 403, """{ "error": { "code": "Authorization_RequestDenied", "message": "denied" } }"""))));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            new[] { "/users/u1" });

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("BatchItemError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("Authorization_RequestDenied", error.Exception.Message);
        Assert.Contains(result.Warnings, w => w.Contains("failed after all retry attempts"));
    }

    [Fact]
    public void Nothing_piped_in_sends_nothing()
    {
        var handler = Batch(Responses(Item(1, 200)));
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxBatchRequest").AddParameter("Confirm", false),
            Array.Empty<object>());

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
    }
}
