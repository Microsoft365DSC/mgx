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
    public void DeltaState_ValidateWriteAccess_creates_missing_directory_and_leaves_no_probe()
    {
        var nested = Path.Combine(_dir, "nested", "deeper", "delta.state");

        DeltaState.ValidateWriteAccess(nested);

        Assert.True(Directory.Exists(Path.GetDirectoryName(nested)!));
        Assert.False(File.Exists(nested + ".probe"));
    }
}
