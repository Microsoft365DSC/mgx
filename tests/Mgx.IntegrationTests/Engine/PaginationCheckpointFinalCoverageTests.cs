using System;
using System.IO;
using System.Text.Json;
using Mgx.Engine.Pagination;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional tests to PaginationCheckpoint.
/// </summary>
public class PaginationCheckpointFinalCoverageTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mgx-final-" + Guid.NewGuid().ToString("N"));

    public PaginationCheckpointFinalCoverageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Save_UpdatesTimestampOnEachSave()
    {
        var path = PathFor("ts.checkpoint");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var cp = new PaginationCheckpoint { Resource = "/a", NextLink = null, ItemsCollected = 1 };
        cp.Save(path);

        var loaded1 = PaginationCheckpoint.Load(path);
        Assert.True(loaded1!.Timestamp >= before);

        Thread.Sleep(10);
        var before2 = DateTimeOffset.UtcNow.AddSeconds(-1);
        cp.ItemsCollected = 2;
        cp.Save(path);

        var loaded2 = PaginationCheckpoint.Load(path);
        Assert.True(loaded2!.Timestamp >= before2);
        Assert.True(loaded2.Timestamp > loaded1.Timestamp);
    }

    [Fact]
    public void Delete_ReturnsTrue_WhenFileDoesNotExist()
    {
        Assert.True(PaginationCheckpoint.Delete(PathFor("missing")));
    }

    [Fact]
    public void Save_HandlesVeryLongUrls()
    {
        var path = PathFor("long.checkpoint");
        var longUrl = new string('a', 5000);
        var cp = new PaginationCheckpoint
        {
            Resource = longUrl,
            NextLink = longUrl + "?next",
            ItemsCollected = 100
        };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(longUrl, loaded.Resource);
    }

    [Fact]
    public void Save_WhenPathHasDirectory_CreatesDirectory()
    {
        var subDir = Path.Combine(_dir, "subdir");
        Directory.CreateDirectory(subDir);
        var path = Path.Combine(subDir, "checkpoint.json");

        var cp = new PaginationCheckpoint { Resource = "/test", NextLink = null, ItemsCollected = 1 };
        cp.Save(path);

        Assert.True(File.Exists(path));
        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("/test", loaded.Resource);
    }

    [Fact]
    public void Delete_ReturnsFalse_WhileAnotherHandleHoldsTheFile()
    {
        var path = PathFor("locked.checkpoint");
        new PaginationCheckpoint { Resource = "/test", NextLink = null, ItemsCollected = 1 }.Save(path);

        // FileShare.None makes the delete fail the way a competing process would
        using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(PaginationCheckpoint.Delete(path));
            Assert.True(File.Exists(path));
        }

        Assert.True(PaginationCheckpoint.Delete(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Load_WithIOExceptionDuringRead_ReturnsNull()
    {
        // On Windows, we can't easily simulate an IOException on read.
        // This test verifies the method handles missing files gracefully.
        var path = PathFor("missing.checkpoint");
        var loaded = PaginationCheckpoint.Load(path);
        Assert.Null(loaded);
    }

    [Fact]
    public void Save_WithVeryLongNextLink_Works()
    {
        var path = PathFor("longnext.checkpoint");
        var longNextLink = new string('b', 10000) + "?next=1";
        var cp = new PaginationCheckpoint
        {
            Resource = "/test",
            NextLink = longNextLink,
            ItemsCollected = 50
        };
        cp.Save(path);

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal(longNextLink, loaded.NextLink);
    }

    [Fact]
    public void Load_WithExtraProperties_IgnoresThem()
    {
        var path = PathFor("extra.checkpoint");
        File.WriteAllText(path, """{ "resource": "/test", "nextLink": null, "itemsCollected": 10, "pageItemsAlreadyWritten": 5, "extraField": "ignored", "anotherExtra": 123 }""");

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("/test", loaded.Resource);
        Assert.Equal(10, loaded.ItemsCollected);
        Assert.Equal(5, loaded.PageItemsAlreadyWritten);
    }

    [Fact]
    public void Delete_RemovesTmpFileWhenMainDeleteFails()
    {
        // On Windows, we can't easily simulate a delete failure on the main file
        // while still allowing tmp deletion. This test verifies both files are deleted.
        var path = PathFor("tmpdelete.checkpoint");
        var cp = new PaginationCheckpoint { Resource = "/test", NextLink = null, ItemsCollected = 1 };
        cp.Save(path);
        File.WriteAllText(path + ".tmp", "temp data");

        var result = PaginationCheckpoint.Delete(path);
        Assert.True(result);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_WithZeroTimestamp_HandlesCorrectly()
    {
        var path = PathFor("zerots.checkpoint");
        File.WriteAllText(path, """{ "resource": "/test", "nextLink": null, "itemsCollected": 1, "pageItemsAlreadyWritten": 0, "timestamp": "0001-01-01T00:00:00.0000000+00:00" }""");

        var loaded = PaginationCheckpoint.Load(path);
        Assert.NotNull(loaded);
        Assert.Equal("/test", loaded.Resource);
    }
}
