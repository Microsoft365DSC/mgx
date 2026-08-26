using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// DeltaState and PaginationCheckpoint both persist resume state with an atomic
/// write (temp file + rename). A corrupt file must degrade to "start over" rather
/// than throwing, otherwise a crashed run leaves the cmdlet permanently broken.
/// </summary>
public class StatePersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mgx-tests-" + Guid.NewGuid().ToString("N"));

    public StatePersistenceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private string InTempDir(string name) => Path.Combine(_dir, name);

    [Fact]
    public void DeltaState_round_trips_through_disk()
    {
        var path = InTempDir("delta.state");
        var saved = new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc",
            Resource = "/users/delta",
            Select = "id,displayName",
            Filter = "startsWith(displayName,'A')",
            ItemCount = 42,
            GraphEndpoint = "https://graph.microsoft.com"
        };

        saved.Save(path);
        var (loaded, result) = DeltaState.LoadWithResult(path);

        Assert.Equal(DeltaLoadResult.Ok, result);
        Assert.NotNull(loaded);
        Assert.Equal(saved.DeltaLink, loaded.DeltaLink);
        Assert.Equal(saved.Resource, loaded.Resource);
        Assert.Equal(saved.Select, loaded.Select);
        Assert.Equal(saved.Filter, loaded.Filter);
        Assert.Equal(42, loaded.ItemCount);
        Assert.Equal(saved.GraphEndpoint, loaded.GraphEndpoint);
    }

    [Fact]
    public void DeltaState_Save_stamps_LastSync_and_leaves_no_temp_file()
    {
        var path = InTempDir("stamped.state");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        new DeltaState { DeltaLink = "https://graph.microsoft.com/v1.0/users/delta" }.Save(path);

        var loaded = DeltaState.Load(path);
        Assert.NotNull(loaded);
        Assert.True(loaded.LastSync >= before);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void DeltaState_distinguishes_missing_from_corrupt()
    {
        var missing = InTempDir("does-not-exist.state");
        Assert.Equal(DeltaLoadResult.NotFound, DeltaState.LoadWithResult(missing).Result);

        var corrupt = InTempDir("corrupt.state");
        File.WriteAllText(corrupt, "{ this is not json");

        var (state, result) = DeltaState.LoadWithResult(corrupt);
        Assert.Equal(DeltaLoadResult.Corrupt, result);
        Assert.Null(state);
        // Backward-compatible Load collapses both cases to null
        Assert.Null(DeltaState.Load(corrupt));
    }

    [Fact]
    public void DeltaState_Delete_removes_state_and_temp_and_tolerates_absence()
    {
        var path = InTempDir("delete-me.state");
        new DeltaState { DeltaLink = "https://graph.microsoft.com/v1.0/users/delta" }.Save(path);
        File.WriteAllText(path + ".tmp", "leftover");

        Assert.True(DeltaState.Delete(path));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        // Deleting a file that is already gone is success, not failure
        Assert.True(DeltaState.Delete(path));
    }

    [Fact]
    public void DeltaState_ValidateWriteAccess_creates_missing_directory_and_leaves_no_probe()
    {
        var nested = Path.Combine(_dir, "nested", "deeper", "delta.state");

        DeltaState.ValidateWriteAccess(nested);

        Assert.True(Directory.Exists(Path.GetDirectoryName(nested)!));
        Assert.False(File.Exists(nested + ".probe"));
    }

    [Fact]
    public void PaginationCheckpoint_round_trips_and_preserves_resume_offset()
    {
        var path = InTempDir("page.checkpoint");
        new PaginationCheckpoint
        {
            Resource = "/users",
            NextLink = "https://graph.microsoft.com/v1.0/users?$skiptoken=abc",
            ItemsCollected = 1500,
            PageItemsAlreadyWritten = 37
        }.Save(path);

        var loaded = PaginationCheckpoint.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal("/users", loaded.Resource);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", loaded.NextLink);
        Assert.Equal(1500, loaded.ItemsCollected);
        // Without this the resumed run re-emits 37 items it already wrote
        Assert.Equal(37, loaded.PageItemsAlreadyWritten);
    }

    [Fact]
    public void PaginationCheckpoint_defaults_resume_offset_for_pre_existing_checkpoints()
    {
        // Checkpoints written before PageItemsAlreadyWritten existed must still load
        var path = InTempDir("legacy.checkpoint");
        File.WriteAllText(path, """
            { "resource": "/users", "nextLink": null, "itemsCollected": 10 }
            """);

        var loaded = PaginationCheckpoint.Load(path);

        Assert.NotNull(loaded);
        Assert.Equal(0, loaded.PageItemsAlreadyWritten);
    }

    [Fact]
    public void PaginationCheckpoint_returns_null_for_missing_or_corrupt_file()
    {
        Assert.Null(PaginationCheckpoint.Load(InTempDir("nope.checkpoint")));

        var corrupt = InTempDir("corrupt.checkpoint");
        File.WriteAllText(corrupt, "<<<not json>>>");
        Assert.Null(PaginationCheckpoint.Load(corrupt));
    }
}
