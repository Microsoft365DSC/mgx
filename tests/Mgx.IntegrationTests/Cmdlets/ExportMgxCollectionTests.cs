using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Cmdlets.Export;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Export-MgxCollection cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class ExportMgxCollectionTests
{
    [Fact]
    public void ProcessRecord_SearchRequiresConsistencyLevel_ThrowsError()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"value":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Export-MgxCollection")
                .AddParameter("Uri", "/users")
                .AddParameter("OutputFile", Path.GetTempFileName())
                .AddParameter("Search", "test");
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("ConsistencyLevelRequired", result.Terminating.FullyQualifiedErrorId);
    }

    [Fact]
    public void ProcessRecord_CheckpointPathEqualsOutputFile_ThrowsError()
    {
        var tempFile = Path.GetTempFileName();
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"value":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Export-MgxCollection")
                .AddParameter("Uri", "/users")
                .AddParameter("OutputFile", tempFile)
                .AddParameter("CheckpointPath", tempFile);
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("CheckpointOutputCollision", result.Terminating.FullyQualifiedErrorId);

        File.Delete(tempFile);
    }

    [Fact]
    public void PaginationCheckpoint_Load_RoundTrips()
    {
        var tempPath = Path.GetTempFileName();
        var checkpoint = new PaginationCheckpoint
        {
            Resource = "https://graph.microsoft.com/v1.0/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            ItemsCollected = 100,
            PageItemsAlreadyWritten = 50
        };

        checkpoint.Save(tempPath);

        var loaded = PaginationCheckpoint.Load(tempPath);

        Assert.NotNull(loaded);
        Assert.Equal(checkpoint.Resource, loaded.Resource);
        Assert.Equal(checkpoint.NextLink, loaded.NextLink);
        Assert.Equal(checkpoint.ItemsCollected, loaded.ItemsCollected);
        Assert.Equal(checkpoint.PageItemsAlreadyWritten, loaded.PageItemsAlreadyWritten);

        File.Delete(tempPath);
    }

    [Fact]
    public void PaginationCheckpoint_Load_MissingFile_ReturnsNull()
    {
        var loaded = PaginationCheckpoint.Load(Path.GetTempFileName() + ".nonexistent");
        Assert.Null(loaded);
    }

    [Fact]
    public void PaginationCheckpoint_Load_CorruptFile_ReturnsNull()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "not valid json");

        var loaded = PaginationCheckpoint.Load(tempPath);
        Assert.Null(loaded);

        File.Delete(tempPath);
    }

    [Fact]
    public void PaginationCheckpoint_Delete_RemovesFile()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "test");

        PaginationCheckpoint.Delete(tempPath);

        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public void ResumeState_Properties_SetCorrectly()
    {
        var resume = new ResumeState(
            "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            50,
            100);

        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", resume.NextLink);
        Assert.Equal(50, resume.SkipOnFirstPage);
        Assert.Equal(100, resume.ItemsAlreadyCollected);
    }
}
