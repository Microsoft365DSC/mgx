using System.Management.Automation;
using System.Net;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests.Cmdlets.Base;

/// <summary>
/// The shared URL builder and the crash-recovery file helpers, reached through a probe subclass
/// because both are protected on MgxCmdletBase.
/// </summary>
public class RecoveryAndUrlHelperTests : IDisposable
{
    private sealed class Probe : MgxCmdletBase
    {
        public static string Url(string relativeUri,
            bool noPageSize = false, int top = 0, int pageSize = 999, string? filter = null,
            string[]? property = null, string[]? sort = null, string? search = null,
            int skip = 0, string[]? expand = null, bool includeCount = false) =>
            BuildListUrl("https://graph.microsoft.com/v1.0", relativeUri,
                new ODataListParams(noPageSize, top, pageSize, filter, property, sort, search,
                    skip, expand, includeCount));

        public static ErrorCategory Category(HttpStatusCode status) => MapStatusToCategory(status);

        public static bool Adopt(string outputPath, long itemCount) =>
            TryAdoptOrphanedTemp(outputPath, itemCount, out _, out _, out _);
    }

    private readonly string _workDir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "mgx-recovery-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string InWorkDir(string name) => Path.Combine(_workDir, name);

    private static string TempNameFor(string outputFileName) =>
        $"{outputFileName}.{Guid.NewGuid():N}.tmp";

    // --- URL building ---

    [Fact]
    public void A_plain_collection_carries_only_the_page_size()
    {
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$top=999",
            Probe.Url("/users"));
    }

    [Fact]
    public void Top_below_the_page_size_becomes_the_page_size()
    {
        Assert.Contains("$top=5", Probe.Url("/users", top: 5));
    }

    [Fact]
    public void NoPageSize_drops_the_top_entirely()
    {
        Assert.DoesNotContain("$top=", Probe.Url("/users", noPageSize: true));
    }

    [Fact]
    public void Every_odata_option_lands_in_the_query()
    {
        var url = Probe.Url("/users",
            noPageSize: true,
            filter: "startsWith(displayName,'a')",
            property: ["id", "displayName"],
            sort: ["displayName desc"],
            skip: 10,
            expand: ["manager"],
            includeCount: true);

        Assert.Contains("$filter=startsWith", url);
        Assert.Contains("$select=id%2CdisplayName", url);
        Assert.Contains("$orderby=displayName%20desc", url);
        Assert.Contains("$skip=10", url);
        Assert.Contains("$expand=manager", url);
        Assert.Contains("$count=true", url);
    }

    [Fact]
    public void A_search_term_is_quoted_and_brings_the_count_with_it()
    {
        var url = Probe.Url("/users", noPageSize: true, search: "displayName:ann");

        Assert.Contains("%22displayName%3Aann%22", url);
        Assert.Contains("$count=true", url);
    }

    [Fact]
    public void An_already_quoted_search_term_is_not_quoted_twice()
    {
        var url = Probe.Url("/users", noPageSize: true, search: "\"displayName:ann\"");

        Assert.DoesNotContain("%22%22", url);
    }

    [Fact]
    public void Options_append_to_a_uri_that_already_carries_a_query()
    {
        var url = Probe.Url("/users?$filter=a eq 1", skip: 2, noPageSize: true);

        Assert.Contains("?$filter=a eq 1&$skip=2", url);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, ErrorCategory.ObjectNotFound)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCategory.AuthenticationError)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCategory.PermissionDenied)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCategory.InvalidArgument)]
    [InlineData(HttpStatusCode.Conflict, ErrorCategory.ResourceExists)]
    [InlineData((HttpStatusCode)429, ErrorCategory.LimitsExceeded)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCategory.ResourceUnavailable)]
    public void A_status_maps_to_the_category_powershell_reports(HttpStatusCode status, ErrorCategory expected)
    {
        Assert.Equal(expected, Probe.Category(status));
    }

    // --- trimming an output back to its checkpoint ---

    [Fact]
    public void The_newest_matching_temp_is_adopted_line_for_line()
    {
        var path = InWorkDir("adopt.jsonl");
        var tempName = TempNameFor("adopt.jsonl");
        File.WriteAllLines(InWorkDir(tempName), ["a", "b", "c"]);

        Assert.True(Probe.Adopt(path, 2));
        Assert.Equal(["a", "b"], File.ReadAllLines(path));
        Assert.False(File.Exists(InWorkDir(tempName)));
    }

    [Fact]
    public void A_temp_with_fewer_lines_than_recorded_is_not_adopted()
    {
        var path = InWorkDir("tooshort.jsonl");
        File.WriteAllLines(InWorkDir(TempNameFor("tooshort.jsonl")), ["a"]);

        Assert.False(Probe.Adopt(path, 5));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Adoption_is_refused_when_an_output_already_exists()
    {
        var path = InWorkDir("existing.jsonl");
        File.WriteAllText(path, "keep me");
        File.WriteAllLines(InWorkDir(TempNameFor("existing.jsonl")), ["a", "b"]);

        Assert.False(Probe.Adopt(path, 1));
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public void Adoption_is_refused_when_no_temp_survived()
    {
        Assert.False(Probe.Adopt(InWorkDir("nothing.jsonl"), 3));
    }

    [Fact]
    public void Adoption_of_nothing_is_refused()
    {
        Assert.False(Probe.Adopt(InWorkDir("zero.jsonl"), 0));
    }
}
