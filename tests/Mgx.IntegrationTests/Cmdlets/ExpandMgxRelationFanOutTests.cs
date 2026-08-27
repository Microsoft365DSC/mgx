using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The fan-out and attachment behaviour of Expand-MgxRelation against a stub transport.
/// </summary>
[Collection("Pipeline")]
public class ExpandMgxRelationFanOutTests
{
    private static Hashtable User(string id) => new() { ["id"] = id, ["displayName"] = id };

    private static string Owner(HttpRequestMessage request) =>
        request.RequestUri!.Segments[^2].TrimEnd('/');

    private static StubHttpMessageHandler Collection(params string[] memberSuffixes) =>
        new StubHttpMessageHandler().Enqueue(request =>
        {
            var owner = Owner(request);
            var items = string.Join(",", memberSuffixes.Select(s => $$"""{ "id": "{{owner}}-{{s}}" }"""));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "value": [ {{items}} ] }""", Encoding.UTF8, "application/json")
            };
        });

    private static StubHttpMessageHandler AlwaysStatus(HttpStatusCode status) =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(
                """{ "error": { "code": "Failed", "message": "nope" } }""",
                Encoding.UTF8, "application/json")
        });

    private static object? Relation(PSObject output, string name) =>
        ((Hashtable)output.BaseObject)[name];

    [Fact]
    public void A_uri_without_the_placeholder_is_refused_up_front()
    {
        using var host = new MgxTestHost(Collection("m1"));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/members")
                .AddParameter("As", "members"),
            new[] { User("g1") });

        Assert.NotNull(result.Terminating);
        Assert.Equal("MissingIdPlaceholder", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_search_uri_without_a_consistency_level_is_refused_up_front()
    {
        using var host = new MgxTestHost(Collection("m1"));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members?$search=\"displayName:a\"")
                .AddParameter("As", "members"),
            new[] { User("g1") });

        Assert.NotNull(result.Terminating);
        Assert.Equal("ConsistencyLevelRequired", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_collection_relation_is_attached_as_an_array_per_input()
    {
        var handler = Collection("m1", "m2");
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            new[] { User("g1"), User("g2") });

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, result.Output.Count);
        foreach (var output in result.Output)
        {
            var members = Assert.IsType<Hashtable[]>(Relation(output, "members"));
            Assert.Equal(2, members.Length);
        }
    }

    [Fact]
    public void A_singleton_relation_is_still_attached_as_a_one_element_array()
    {
        var handler = new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "id": "boss" }""", Encoding.UTF8, "application/json")
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/users/{id}/manager")
                .AddParameter("As", "manager"),
            new[] { User("u1") });

        var manager = Assert.IsType<Hashtable[]>(Relation(Assert.Single(result.Output), "manager"));
        Assert.Equal("boss", Assert.Single(manager)["id"]);
    }

    [Fact]
    public void Flatten_unwraps_a_single_item_and_warns_when_there_is_more_than_one()
    {
        using var host = new MgxTestHost(Collection("m1", "m2"));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members")
                .AddParameter("Flatten"),
            new[] { User("g1") });

        Assert.IsType<Hashtable[]>(Relation(Assert.Single(result.Output), "members"));
        Assert.Contains(result.Warnings, w => w.Contains("returned 2 items instead of 1"));
    }

    [Fact]
    public void Flatten_of_a_single_item_relation_attaches_the_item_itself()
    {
        using var host = new MgxTestHost(Collection("m1"));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members")
                .AddParameter("Flatten"),
            new[] { User("g1") });

        var member = Assert.IsType<Hashtable>(Relation(Assert.Single(result.Output), "members"));
        Assert.Equal("g1-m1", member["id"]);
    }

    [Fact]
    public void Two_inputs_sharing_an_id_are_fetched_once_and_get_their_own_hashtables()
    {
        var handler = Collection("m1");
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            new[] { User("g1"), User("g1") });

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(2, result.Output.Count);
        var first = Assert.IsType<Hashtable[]>(Relation(result.Output[0], "members"))[0];
        var second = Assert.IsType<Hashtable[]>(Relation(result.Output[1], "members"))[0];
        Assert.NotSame(first, second);
    }

    [Fact]
    public void An_input_without_the_id_property_is_reported_and_still_emitted()
    {
        var handler = Collection("m1");
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            new[] { new Hashtable { ["displayName"] = "no id" } });

        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(result.Errors,
            e => e.FullyQualifiedErrorId.StartsWith("MissingIdProperty", StringComparison.Ordinal));
        Assert.Null(Relation(Assert.Single(result.Output), "members"));
    }

    [Fact]
    public void IdProperty_picks_the_member_the_ids_are_read_from()
    {
        var handler = Collection("m1");
        using var host = new MgxTestHost(handler);

        host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members")
                .AddParameter("IdProperty", "groupId"),
            new[] { new Hashtable { ["groupId"] = "g9" } });

        Assert.Equal("https://graph.microsoft.com/v1.0/groups/g9/members", handler.Requests[0].Uri);
    }

    [Fact]
    public void Top_is_pushed_to_graph_and_enforced_on_what_comes_back()
    {
        var handler = Collection("m1", "m2", "m3");
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members")
                .AddParameter("Top", 2),
            new[] { User("g1") });

        Assert.Contains("$top=2", handler.Requests[0].Uri);
        Assert.Equal(2, Assert.IsType<Hashtable[]>(Relation(Assert.Single(result.Output), "members")).Length);
    }

    [Fact]
    public void A_top_the_caller_already_wrote_into_the_uri_is_not_duplicated()
    {
        var handler = Collection("m1");
        using var host = new MgxTestHost(handler);

        host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members?$top=5")
                .AddParameter("As", "members")
                .AddParameter("Top", 2),
            new[] { User("g1") });

        var uri = handler.Requests[0].Uri;
        Assert.Contains("$top=5", uri);
        Assert.DoesNotContain("$top=2", uri);
    }

    [Fact]
    public void A_failed_relation_is_reported_against_its_entity_id()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.NotFound));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            new[] { User("g1") });

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("ExpandRelationError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("g1", error.TargetObject);
        Assert.Null(Relation(Assert.Single(result.Output), "members"));
    }

    [Fact]
    public void SkipForbidden_replaces_the_denials_with_one_summary_warning()
    {
        using var host = new MgxTestHost(AlwaysStatus(HttpStatusCode.Forbidden));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members")
                .AddParameter("SkipForbidden"),
            new[] { User("g1"), User("g2") });

        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, w => w.Contains("Skipped 2 entities"));
    }

    [Fact]
    public void A_relation_attaches_to_a_PSCustomObject_as_a_note_property()
    {
        using var host = new MgxTestHost(Collection("m1"));

        var input = new PSObject();
        input.Properties.Add(new PSNoteProperty("id", "g1"));

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            new[] { input });

        var members = Assert.Single(result.Output).Properties["members"].Value;
        Assert.Single(Assert.IsType<Hashtable[]>(members));
    }

    [Fact]
    public void Nothing_piped_in_means_nothing_fetched()
    {
        var handler = Collection("m1");
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Expand-MgxRelation")
                .AddParameter("Uri", "/groups/{id}/members")
                .AddParameter("As", "members"),
            Array.Empty<object>());

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
    }
}
