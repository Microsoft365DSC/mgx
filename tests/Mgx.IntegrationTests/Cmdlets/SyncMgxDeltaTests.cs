using System.Management.Automation;
using System.Net;
using System.Text.Json;
using Mgx.Cmdlets.Cmdlets.Delta;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;
using Mgx.Engine.Models;
using Mgx.Engine.Pagination;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Sync-MgxDelta cmdlet.
/// </summary>
// Hosts cmdlets through MgxTestHost, which sets the shared transport and client options
[Collection("Pipeline")]
public class SyncMgxDeltaTests
{
    // Use reflection to test private methods - cast to non-nullable since we control the test setup
    private static T InvokeMethod<T>(Type type, string methodName, params object?[] parameters)
    {
        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = method!.Invoke(null, parameters);
        return (T)result!;
    }
    private static GraphCollectionResponse<JsonElement> CreateDeltaPage(params (string id, string? deltaLink)[] items)
    {
        var value = new List<JsonElement>();
        foreach (var item in items)
        {
            var json = JsonSerializer.Serialize(new { id = item.id });
            value.Add(JsonDocument.Parse(json).RootElement);
        }

        var response = new GraphCollectionResponse<JsonElement>
        {
            Value = value.ToArray(),
            NextLink = items.Length > 0 && !string.IsNullOrEmpty(items[^1].deltaLink) ? items[^1].deltaLink : null,
            Count = items.Length
        };
        return response;
    }

    [Fact]
    public void ProcessRecord_FullSync_DeletesExistingState()
    {
        var handler = new StubHttpMessageHandler()
            .EnqueueJson(HttpStatusCode.OK, JsonSerializer.Serialize(CreateDeltaPage(("1", null))));
        using var host = new MgxTestHost(handler);

        // Create a dummy delta state file
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "old state");

        var result = host.Run(ps =>
        {
            ps.AddCommand("Sync-MgxDelta")
                .AddParameter("Uri", "/users/delta")
                .AddParameter("DeltaPath", tempPath)
                .AddParameter("FullSync", true);
        });

        Assert.False(File.Exists(tempPath), "FullSync should delete existing delta state file");
    }

    [Fact]
    public void ProcessRecord_InvalidUri_ThrowsError()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"value":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Sync-MgxDelta")
                .AddParameter("Uri", "https://graph.microsoft.com/v1.0/users/delta")
                .AddParameter("DeltaPath", Path.GetTempFileName());
        });

        Assert.NotNull(result.Terminating);
        Assert.Contains("AbsoluteUriNotAllowed", result.Terminating.FullyQualifiedErrorId);
    }

    [Fact]
    public void ProcessRecord_NonDeltaUri_WritesWarning()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.OK, """{"value":[]}""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps =>
        {
            ps.AddCommand("Sync-MgxDelta")
                .AddParameter("Uri", "/users")
                .AddParameter("DeltaPath", Path.GetTempFileName());
        });

        Assert.Contains(result.Warnings, w => w.Contains("/delta"));
    }

    [Fact]
    public void NormalizeSelect_HandlesNullAndEmpty()
    {
        var method = typeof(SyncMgxDelta).GetMethod("NormalizeSelect",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.Equal("", method!.Invoke(null, [null]));
        Assert.Equal("", method.Invoke(null, [""]));
    }

    [Fact]
    public void NormalizeSelect_DeduplicatesAndSorts()
    {
        var result = InvokeMethod<string>(typeof(SyncMgxDelta), "NormalizeSelect", "displayName,id,displayName,Id");
        Assert.Equal("displayName,id", result);
    }

    [Fact]
    public void NormalizeSelect_TrimsWhitespace()
    {
        var result = InvokeMethod<string>(typeof(SyncMgxDelta), "NormalizeSelect", " id , displayName ");
        Assert.Equal("displayName,id", result);
    }

    [Fact]
    public void DeltaState_LoadWithResult_HandlesMissingFile()
    {
        var (state, result) = DeltaState.LoadWithResult(Path.GetTempFileName() + ".nonexistent");
        Assert.Null(state);
        Assert.Equal(DeltaLoadResult.NotFound, result);
    }

    [Fact]
    public void DeltaState_LoadWithResult_HandlesCorruptFile()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "not valid json");

        var (state, result) = DeltaState.LoadWithResult(tempPath);
        Assert.Null(state);
        Assert.Equal(DeltaLoadResult.Corrupt, result);

        File.Delete(tempPath);
    }

    [Fact]
    public void DeltaState_ValidateWriteAccess_CreatesDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mgx_test_" + Guid.NewGuid());
        var testFile = Path.Combine(tempDir, "test.json");

        DeltaState.ValidateWriteAccess(testFile);

        Assert.True(Directory.Exists(tempDir));
        Directory.Delete(tempDir, true);
    }

    [Fact]
    public void DeltaState_SaveAndLoad_RoundTrips()
    {
        var tempPath = Path.GetTempFileName();
        var original = new DeltaState
        {
            DeltaLink = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc",
            Select = "id,displayName",
            Filter = "startswith(displayName,'A')",
            Resource = "/users/delta",
            ItemCount = 42,
            GraphEndpoint = "https://graph.microsoft.com"
        };

        original.Save(tempPath);

        var loaded = DeltaState.Load(tempPath);

        Assert.NotNull(loaded);
        Assert.Equal(original.DeltaLink, loaded.DeltaLink);
        Assert.Equal(original.Select, loaded.Select);
        Assert.Equal(original.Filter, loaded.Filter);
        Assert.Equal(original.Resource, loaded.Resource);
        Assert.Equal(original.ItemCount, loaded.ItemCount);
        Assert.Equal(original.GraphEndpoint, loaded.GraphEndpoint);

        File.Delete(tempPath);
    }

    [Fact]
    public void DeltaState_Delete_RemovesFile()
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, "test");

        var result = DeltaState.Delete(tempPath);

        Assert.True(result);
        Assert.False(File.Exists(tempPath));
    }
}