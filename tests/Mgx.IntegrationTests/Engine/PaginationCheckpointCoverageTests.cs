using System;
using System.IO;
using System.Text.Json;
using Mgx.Engine.Pagination;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional coverage tests for PaginationCheckpoint.
/// </summary>
public class PaginationCheckpointCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mgx-paging-" + Guid.NewGuid().ToString("N"));

    public PaginationCheckpointCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = PathFor("overwrite.checkpoint");
        new PaginationCheckpoint { Resource = "/a", NextLink = "next", ItemsCollected = 1 }.Save(path);
        new PaginationCheckpoint { Resource = "/b", NextLink = null, ItemsCollected = 2 }.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("/b", loaded.Resource);
        Assert.Null(loaded.NextLink);
        Assert.Equal(2, loaded.ItemsCollected);
    }

    [Fact]
    public void Save_HandlesSpecialCharactersInUrl()
    {
        var path = PathFor("special.checkpoint");
        var cp = new PaginationCheckpoint
        {
            Resource = "/users?$filter=startsWith(name,'test')",
            NextLink = "/users?$filter=startsWith(name,'test')&$skiptoken=abc",
            ItemsCollected = 10
        };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Contains("startsWith", loaded.Resource);
    }

    [Fact]
    public void Delete_NonExistentFile_ReturnsTrue()
    {
        Assert.True(PaginationCheckpoint.Delete(PathFor("nonexistent.checkpoint")));
    }

    [Fact]
    public void Load_WithNullNextLink_HandlesCorrectly()
    {
        var path = PathFor("nullnext.checkpoint");
        File.WriteAllText(path, """
            { "resource": "/groups", "nextLink": null, "itemsCollected": 42, "pageItemsAlreadyWritten": 0 }
            """);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Null(loaded.NextLink);
    }

    [Fact]
    public void Save_WithNegativeItemsCollected_HandlesCorrectly()
    {
        var path = PathFor("negative.checkpoint");
        var cp = new PaginationCheckpoint { Resource = "/test", NextLink = null, ItemsCollected = -1 };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(-1, loaded.ItemsCollected);
    }
}
