using System.Collections;
using System.Net;
using System.Text;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The {id} fan-out paths of Invoke-MgxRequest, which the single-URI tests never reach.
/// </summary>
[Collection("Pipeline")]
public class InvokeMgxRequestFanOutTests
{
    private static StubHttpMessageHandler Entities() =>
        new StubHttpMessageHandler().Enqueue(request =>
        {
            var id = request.RequestUri!.Segments[^1].TrimEnd('/');
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "id": "{{id}}" }""", Encoding.UTF8, "application/json")
            };
        });

    private static StubHttpMessageHandler AlwaysStatus(HttpStatusCode status, string code) =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                $$"""{ "error": { "code": "{{code}}", "message": "nope" } }""",
                Encoding.UTF8, "application/json")
        });

    private static string SourceId(PSObjectLike item) => item.SourceId;

    private sealed record PSObjectLike(string Id, string SourceId);

    private static List<PSObjectLike> Items(MgxResult result) =>
        [.. result.Output.Select(o =>
        {
            var ht = (Hashtable)o.BaseObject;
            return new PSObjectLike((string)ht["id"]!, (string)ht["_MgxSourceId"]!);
        })];

    [Fact]
    public void Pipeline_input_without_an_id_is_an_error_rather_than_a_request()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { new Hashtable { ["displayName"] = "no id here" } });

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(result.Errors,
            e => e.FullyQualifiedErrorId.StartsWith("MissingPipelineId", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_piped_id_runs_without_the_fan_out_machinery()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { "u1" });

        var item = Assert.Single(Items(result));
        Assert.Equal("u1", item.Id);
        Assert.Equal("u1", item.SourceId);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/u1", handler.Requests[0].Uri);
    }

    [Fact]
    public void Every_piped_id_comes_back_tagged_with_the_id_it_came_from()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { "u1", "u2", "u3" });

        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(["u1", "u2", "u3"], Items(result).Select(SourceId).Order());
    }

    [Fact]
    public void Repeated_ids_are_fetched_once()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { "u1", "u2", "u1" });

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, result.Output.Count);
    }

    [Fact]
    public void An_id_cannot_break_out_of_its_path_segment()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { "a/../groups" });

        Assert.Equal("https://graph.microsoft.com/v1.0/users/a%2F..%2Fgroups", handler.Requests[0].Uri);
    }

    [Fact]
    public void A_count_variable_across_several_ids_is_refused_as_ambiguous()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}/messages")
                .AddParameter("CountVariable", "total")
                .AddParameter("All"),
            new[] { "u1", "u2" });

        Assert.NotNull(result.Terminating);
        Assert.Equal("CountVariableNotSupported", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public void Collection_mode_fan_out_walks_a_collection_per_id()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var owner = request.RequestUri!.Segments[^2].TrimEnd('/');
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{ "value": [ { "id": "{{owner}}-m1" }, { "id": "{{owner}}-m2" } ] }""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}/messages")
                .AddParameter("All"),
            new[] { "u1", "u2" });

        Assert.Equal(4, result.Output.Count);
        Assert.Equal(["u1", "u1", "u2", "u2"], Items(result).Select(SourceId).Order());
    }

    [Fact]
    public void A_failing_id_is_reported_against_its_own_key_and_the_rest_still_come_back()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            var id = request.RequestUri!.Segments[^1].TrimEnd('/');
            return id == "missing"
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent(
                        """{ "error": { "code": "Request_ResourceNotFound", "message": "not found" } }""",
                        Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{ "id": "{{id}}" }""", Encoding.UTF8, "application/json")
                };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/{id}"),
            new[] { "u1", "missing", "u2" });

        Assert.Equal(2, result.Output.Count);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("FanOutError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("missing", error.TargetObject);
    }

    [Fact]
    public void SkipNotFound_turns_the_misses_into_one_summary_warning()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.NotFound, "Request_ResourceNotFound"));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("SkipNotFound"),
            new[] { "a", "b" });

        Assert.Empty(result.Output);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("Skipped 2 entities"));
    }

    [Fact]
    public void SkipForbidden_turns_the_denials_into_one_summary_warning()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.Forbidden, "Authorization_RequestDenied"));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("SkipForbidden"),
            new[] { "a", "b" });

        Assert.Empty(result.Output);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("403"));
    }

    [Fact]
    public void A_bulk_write_sends_one_request_per_id_and_emits_what_came_back()
    {
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            var id = request.RequestUri!.Segments[^1].TrimEnd('/');
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "id": "{{id}}" }""", Encoding.UTF8, "application/json")
            };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("Method", "PATCH")
                .AddParameter("Body", """{ "department": "Sales" }""")
                .AddParameter("Confirm", false),
            new[] { "u1", "u2" });

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(["u1", "u2"], Items(result).Select(SourceId).Order());
    }

    [Fact]
    public void A_bulk_write_under_WhatIf_never_reaches_the_network()
    {
        var handler = Entities();
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("Method", "DELETE")
                .AddParameter("WhatIf"),
            new[] { "u1", "u2" });

        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(result.Output);
    }

    [Fact]
    public void A_bulk_write_failure_names_the_id_it_belongs_to()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.NotFound, "Request_ResourceNotFound"));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("Method", "DELETE")
                .AddParameter("Confirm", false),
            new[] { "u1", "u2" });

        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors,
            e => Assert.StartsWith("BulkWriteError", e.FullyQualifiedErrorId, StringComparison.Ordinal));
        Assert.Equal(["u1", "u2"], result.Errors.Select(e => (string)e.TargetObject).Order());
    }

    [Fact]
    public void SkipNotFound_also_covers_the_bulk_write_path()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.NotFound, "Request_ResourceNotFound"));

        var result = host.Run(
            ps => ps.AddCommand("Invoke-MgxRequest")
                .AddParameter("Uri", "/users/{id}")
                .AddParameter("Method", "DELETE")
                .AddParameter("SkipNotFound")
                .AddParameter("Confirm", false),
            new[] { "u1", "u2" });

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("Skipped 2 operations"));
    }

    [Fact]
    public void Skip_warns_that_most_graph_endpoints_ignore_it()
    {
        using var host = new MgxTestHost(Entities());

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Skip", 10));

        Assert.Contains(result.Warnings, w => w.Contains("-Skip"));
    }
}
