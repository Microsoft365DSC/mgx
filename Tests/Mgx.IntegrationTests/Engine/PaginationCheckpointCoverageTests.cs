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
    public void Load_FileNotFound_ReturnsNull()
    {
        Assert.Null(PaginationCheckpoint.Load(PathFor("missing.checkpoint")));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        File.WriteAllText(PathFor("corrupt.checkpoint"), "{ not valid json }");
        Assert.Null(PaginationCheckpoint.Load(PathFor("corrupt.checkpoint")));
    }

    [Fact]
    public void Load_EmptyFile_ReturnsNull()
    {
        File.WriteAllText(PathFor("empty.checkpoint"), "");
        Assert.Null(PaginationCheckpoint.Load(PathFor("empty.checkpoint")));
    }

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
    public void Delete_RemovesBothMainAndTmpFiles()
    {
        var path = PathFor("deleteboth.checkpoint");
        new PaginationCheckpoint { Resource = "/test", NextLink = null, ItemsCollected = 0 }.Save(path);
        File.WriteAllText(path + ".tmp", "temp data");

        Assert.True(PaginationCheckpoint.Delete(path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void RoundTrip_AllPropertiesPreserved()
    {
        var path = PathFor("full.checkpoint");
        var original = new PaginationCheckpoint
        {
            Resource = "/users/delta",
            NextLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=xyz",
            ItemsCollected = 1234,
            PageItemsAlreadyWritten = 56,
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        original.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(original.Resource, loaded.Resource);
        Assert.Equal(original.NextLink, loaded.NextLink);
        Assert.Equal(original.ItemsCollected, loaded.ItemsCollected);
        Assert.Equal(original.PageItemsAlreadyWritten, loaded.PageItemsAlreadyWritten);
        Assert.True(loaded.Timestamp >= original.Timestamp.AddMinutes(-1));
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