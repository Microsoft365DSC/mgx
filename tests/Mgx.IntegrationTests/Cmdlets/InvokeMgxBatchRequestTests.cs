using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mgx.Cmdlets.Cmdlets.Batch;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Invoke-MgxBatchRequest cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class InvokeMgxBatchRequestTests
{
    // Use reflection to test private methods - cast to non-nullable since we control the test setup
    private static T InvokeMethod<T>(object target, string methodName, params object?[] parameters)
    {
        var method = target.GetType().GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(target, parameters);
        return (T)result!;
    }
    [Fact]
    public void ProcessRecord_SearchRequiresConsistencyLevel_ThrowsError()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"responses":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Uri", "/users/{id}/messages?$search=test")
                .AddParameter("Method", "GET");
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("ConsistencyLevelRequired", result.Terminating.FullyQualifiedErrorId);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesAbsoluteUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "v1.0" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "https://graph.microsoft.com/v1.0/users/123");
        Assert.Equal("/users/123", url);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesRelativeUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "v1.0" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "/users/123");
        Assert.Equal("/users/123", url);
    }

    [Fact]
    public void NormalizeToRelativeUrl_HandlesBetaUrl()
    {
        var cmdlet = new InvokeMgxBatchRequest { ApiVersion = "beta" };
        var url = InvokeMethod<string>(cmdlet, "NormalizeToRelativeUrl", "https://graph.microsoft.com/beta/groups/123");
        Assert.Equal("/groups/123", url);
    }


    [Fact]
    public void A_failed_batch_item_is_reported_as_an_error_without_a_dead_letter_path()
    {
        // The per-item errors are what makes -ErrorAction Stop trip and $Error fill in
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {"responses":[
              {"id":"1","status":200,"body":{"id":"u0"}},
              {"id":"2","status":403,"body":{"error":{"code":"Authorization_RequestDenied","message":"denied"}}}
            ]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Uri", new[] { "/users/u0", "/users/u1" })
                .AddParameter("Method", "GET");
        });

        Assert.Null(result.Terminating);
        Assert.Contains(result.Errors, e => e.FullyQualifiedErrorId.Contains("BatchItemError"));
        Assert.Contains(result.Errors, e => e.Exception.Message.Contains("Authorization_RequestDenied"));
    }

    [Fact]
    public void Repeated_instantiation_above_the_documented_rate_does_not_race()
    {
        // The NOTES claimed a race above ~200 invocations/sec. This drives well past that against a
        // stub transport and asserts the cmdlet is instantiable at any rate the host can reach.
        var handler = new StubHttpMessageHandler().EnqueueRepeated(1200, _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"responses":[{"id":"1","status":200,"body":{"id":"u0"}}]}""",
                    System.Text.Encoding.UTF8, "application/json")
            });
        using var host = new MgxTestHost(handler);

        const int invocations = 500;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var failures = 0;
        for (var i = 0; i < invocations; i++)
        {
            var r = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("Uri", new[] { "/users/u0" })
                .AddParameter("Method", "GET"));
            if (r.Terminating != null || r.Output.Count != 1) failures++;
        }
        sw.Stop();

        var perSecond = invocations / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Assert.True(perSecond > 200, $"only reached {perSecond:F0}/sec, the claimed threshold was not exercised");
        Assert.Equal(0, failures);
    }

    [Fact]
    public void Duplicate_urls_are_told_apart_by_their_caller_id()
    {
        // The case Url correlation cannot serve: same URL twice, distinguished only by Id
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """
            {"responses":[
              {"id":"1","status":200,"body":{"n":"first"}},
              {"id":"2","status":200,"body":{"n":"second"}}
            ]}
            """);
        using var host = new MgxTestHost(handler);

        var input = new object[]
        {
            new Hashtable { ["Url"] = "/users/u0", ["Method"] = "GET", ["Id"] = "alpha" },
            new Hashtable { ["Url"] = "/users/u0", ["Method"] = "GET", ["Id"] = "beta" }
        };

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest"), input);

        Assert.Equal(2, result.Output.Count);
        var first  = (Hashtable)result.Output[0].BaseObject;
        var second = (Hashtable)result.Output[1].BaseObject;
        Assert.Equal("alpha", first["Id"]);
        Assert.Equal("beta", second["Id"]);
        Assert.Equal("first",  ((Hashtable)first["Body"]!)["n"]);
        Assert.Equal("second", ((Hashtable)second["Body"]!)["n"]);
    }

    [Fact]
    public void Output_is_unchanged_when_no_caller_id_is_supplied()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK,
            """{"responses":[{"id":"1","status":200,"body":{"id":"u0"}}]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users/u0" })
            .AddParameter("Method", "GET"));

        var row = (Hashtable)result.Output[0].BaseObject;
        Assert.False(row.ContainsKey("Id"));
        Assert.Equal(new[] { "Body", "Method", "Status", "Url" },
            row.Keys.Cast<string>().OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_caller_id_survives_a_dead_letter_round_trip()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK,
            """{"responses":[{"id":"1","status":403,"body":{"error":{"code":"denied"}}}]}""");
        using var host = new MgxTestHost(handler);
        var deadLetter = Path.Combine(Path.GetTempPath(), $"mgx-dl-{Guid.NewGuid():N}.jsonl");

        try
        {
            var input = new object[]
            {
                new Hashtable { ["Url"] = "/users/u0", ["Method"] = "GET", ["Id"] = "correlation-key" }
            };
            host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
                .AddParameter("DeadLetterPath", deadLetter), input);

            var line = File.ReadAllLines(deadLetter).Single();
            using var doc = JsonDocument.Parse(line);
            Assert.Equal("correlation-key", doc.RootElement.GetProperty("Id").GetString());
        }
        finally { File.Delete(deadLetter); }
    }

    private static StubHttpMessageHandler PagingStub(params string[] batchResponses)
    {
        var stub = new StubHttpMessageHandler();
        foreach (var body in batchResponses)
            stub.EnqueueJson(HttpStatusCode.OK, body);
        return stub;
    }

    [Fact]
    public void FollowNextLink_merges_pages_and_drops_the_spent_next_link()
    {
        var handler = PagingStub(
            """
            {"responses":[{"id":"1","status":200,"body":{
              "value":[{"id":"a"}],
              "@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=p2"}}]}
            """,
            """
            {"responses":[{"id":"1","status":200,"body":{"value":[{"id":"b"}]}}]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users" })
            .AddParameter("Method", "GET")
            .AddParameter("FollowNextLink", true));

        var row = (Hashtable)result.Output[0].BaseObject;
        var body = (Hashtable)row["Body"]!;
        var values = ((IEnumerable)body["value"]!).Cast<object>().ToArray();

        Assert.Equal(200, row["Status"]);
        Assert.False(row.ContainsKey("PagingIncomplete"));
        Assert.Equal(2, values.Length);
        Assert.Equal("a", ((Hashtable)values[0])["id"]);
        Assert.Equal("b", ((Hashtable)values[1])["id"]);
        Assert.False(body.ContainsKey("@odata.nextLink"));
    }

    [Fact]
    public void A_failed_page_fails_the_read_and_keeps_what_it_got()
    {
        var handler = PagingStub(
            """
            {"responses":[{"id":"1","status":200,"body":{
              "value":[{"id":"a"}],
              "@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=p2"}}]}
            """,
            """
            {"responses":[{"id":"1","status":403,"body":{"error":{"code":"denied"}}}]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users" })
            .AddParameter("Method", "GET")
            .AddParameter("FollowNextLink", true));

        var row = (Hashtable)result.Output[0].BaseObject;
        var body = (Hashtable)row["Body"]!;

        // The failing page's status, never the first page's 200
        Assert.Equal(403, row["Status"]);
        Assert.True((bool)row["PagingIncomplete"]!);
        Assert.Single(((IEnumerable)body["value"]!).Cast<object>());
        Assert.Contains(result.Errors, e => e.FullyQualifiedErrorId.Contains("BatchPagingFailed"));
    }

    [Fact]
    public void A_next_link_pointing_elsewhere_is_refused_without_killing_the_other_items()
    {
        var handler = PagingStub(
            """
            {"responses":[
              {"id":"1","status":200,"body":{
                "value":[{"id":"a"}],
                "@odata.nextLink":"https://evil.example.com/v1.0/users?$skiptoken=p2"}},
              {"id":"2","status":200,"body":{"value":[{"id":"z"}]}}]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users", "/groups" })
            .AddParameter("Method", "GET")
            .AddParameter("FollowNextLink", true));

        var refused = (Hashtable)result.Output[0].BaseObject;
        var healthy = (Hashtable)result.Output[1].BaseObject;

        Assert.True((bool)refused["PagingIncomplete"]!);
        Assert.Contains(result.Errors, e => e.FullyQualifiedErrorId.Contains("BatchNextLinkRefused"));

        // One bad link must not discard the nineteen good results beside it
        Assert.Equal(200, healthy["Status"]);
        Assert.False(healthy.ContainsKey("PagingIncomplete"));
        Assert.Single(((IEnumerable)((Hashtable)healthy["Body"]!)["value"]!).Cast<object>());
    }

    [Fact]
    public void MaxPage_stops_the_drain_and_says_so()
    {
        var page = """
            {"responses":[{"id":"1","status":200,"body":{
              "value":[{"id":"a"}],
              "@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=more"}}]}
            """;
        var handler = PagingStub(page, page, page);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users" })
            .AddParameter("Method", "GET")
            .AddParameter("FollowNextLink", true)
            .AddParameter("MaxPage", 1));

        var row = (Hashtable)result.Output[0].BaseObject;
        Assert.True((bool)row["PagingIncomplete"]!);
        Assert.Contains(result.Errors, e => e.FullyQualifiedErrorId.Contains("BatchPagingTruncated"));
    }

    [Fact]
    public void Paging_is_off_unless_asked_for()
    {
        var handler = PagingStub("""
            {"responses":[{"id":"1","status":200,"body":{
              "value":[{"id":"a"}],
              "@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=p2"}}]}
            """);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxBatchRequest")
            .AddParameter("Uri", new[] { "/users" })
            .AddParameter("Method", "GET"));

        var body = (Hashtable)((Hashtable)result.Output[0].BaseObject)["Body"]!;

        // One request, and the collection stays partial. Note the caller cannot see that:
        // JsonToHashtable strips @odata.nextLink, which is why -FollowNextLink exists
        Assert.Equal(1, handler.RequestCount);
        Assert.Single(((IEnumerable)body["value"]!).Cast<object>());
        Assert.False(body.ContainsKey("@odata.nextLink"));
    }
}
