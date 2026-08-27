using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mgx.Cmdlets.Cmdlets.Content;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// Get-MgxContent through a runspace, with hop one on the stub transport and hop two on
/// GraphContentClient.DownloadClientForTests.
/// </summary>
[Collection("Pipeline")]
public class GetMgxContentTests : IDisposable
{
    private const string CdnUrl = "https://contoso-my.sharepoint.com/_layouts/15/download.aspx?UniqueId=abc";

    private readonly MockHttpHandler _cdnHandler = new();

    public GetMgxContentTests() =>
        GraphContentClient.DownloadClientForTests = new HttpClient(_cdnHandler);

    public void Dispose()
    {
        GraphContentClient.DownloadClientForTests?.Dispose();
        GraphContentClient.DownloadClientForTests = null;
        GC.SuppressFinalize(this);
    }

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("0123456789");

    private static StubHttpMessageHandler Bytes(byte[] body, HttpStatusCode status = HttpStatusCode.OK,
        string? contentRange = null) =>
        new StubHttpMessageHandler().Enqueue(_ =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            if (contentRange != null)
                content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
            return new HttpResponseMessage(status) { Content = content };
        });

    private static byte[] SingleByteArray(MgxResult result) =>
        Assert.IsType<byte[]>(Assert.Single(result.Output).BaseObject);

    [Fact]
    public void An_absolute_uri_is_refused()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "https://graph.microsoft.com/v1.0/me/drive/items/1/content"));

        Assert.NotNull(result.Terminating);
        Assert.Equal("AbsoluteUriNotAllowed", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void First_and_offset_cannot_be_combined()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content")
            .AddParameter("First", 4)
            .AddParameter("Offset", 2)
            .AddParameter("Length", 2));

        Assert.NotNull(result.Terminating);
        Assert.Equal("RangeParameterConflict", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void Offset_without_length_is_refused()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content")
            .AddParameter("Offset", 2));

        Assert.NotNull(result.Terminating);
        Assert.Equal("OffsetRequiresLength", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_uri_that_is_not_a_content_endpoint_warns()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent").AddParameter("Uri", "/me/drive/items/1"));

        Assert.Contains(result.Warnings, w => w.Contains("does not look like a content endpoint"));
    }

    [Fact]
    public void The_body_reaches_the_pipeline_as_one_byte_array()
    {
        var handler = Bytes(Payload);
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content"));

        Assert.Null(result.Terminating);
        Assert.Equal(Payload, SingleByteArray(result));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/items/1/content", handler.Requests[0].Uri);
    }

    [Fact]
    public void A_range_request_sends_the_range_header_and_emits_only_those_bytes()
    {
        var ranged = Payload[2..5];
        var handler = new StubHttpMessageHandler().Enqueue(request =>
        {
            Assert.Equal("bytes=2-4", request.Headers.Range!.ToString());
            var content = new ByteArrayContent(ranged);
            content.Headers.TryAddWithoutValidation("Content-Range", "bytes 2-4/10");
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content")
            .AddParameter("Offset", 2)
            .AddParameter("Length", 3));

        Assert.Equal(ranged, SingleByteArray(result));
    }

    [Fact]
    public void A_server_that_ignores_the_range_is_trimmed_locally()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content")
            .AddParameter("Offset", 4)
            .AddParameter("Length", 3));

        Assert.Equal(Payload[4..7], SingleByteArray(result));
    }

    [Fact]
    public void First_trims_a_full_body_the_server_sent_anyway()
    {
        using var host = new MgxTestHost(Bytes(Payload));

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content")
            .AddParameter("First", 3));

        Assert.Equal(Payload[..3], SingleByteArray(result));
    }

    [Fact]
    public void OutFile_writes_the_bytes_and_emits_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mgx-content-{Guid.NewGuid():N}.bin");
        using var host = new MgxTestHost(Bytes(Payload));

        try
        {
            var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
                .AddParameter("Uri", "/me/drive/items/1/content")
                .AddParameter("OutFile", path));

            Assert.Null(result.Terminating);
            Assert.Empty(result.Output);
            Assert.Equal(Payload, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OutFile_refuses_a_second_piped_item_rather_than_overwriting_the_first()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mgx-content-{Guid.NewGuid():N}.bin");
        var handler = new StubHttpMessageHandler()
            .EnqueueRepeated(2, _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Payload)
            });
        using var host = new MgxTestHost(handler);

        try
        {
            var result = host.Run(
                ps => ps.AddCommand("Get-MgxContent").AddParameter("OutFile", path),
                new[] { DriveItem("a"), DriveItem("b") });

            Assert.NotNull(result.Terminating);
            Assert.Equal("OutFileWithMultipleInputs", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
            Assert.Equal(Payload, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_piped_drive_item_is_resolved_through_its_drive()
    {
        var handler = Bytes(Payload);
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Get-MgxContent"),
            new[] { DriveItem("item-1", "drive-9") });

        Assert.Equal(Payload, SingleByteArray(result));
        Assert.Equal("https://graph.microsoft.com/v1.0/drives/drive-9/items/item-1/content",
            handler.Requests[0].Uri);
    }

    [Fact]
    public void A_piped_item_without_a_drive_is_an_error_rather_than_a_guess()
    {
        var handler = Bytes(Payload);
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Get-MgxContent"),
            new[] { new Hashtable { ["id"] = "item-1" } });

        Assert.Empty(result.Output);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(result.Errors,
            e => e.FullyQualifiedErrorId.StartsWith("MissingDriveItemInfo", StringComparison.Ordinal));
    }

    [Fact]
    public void A_piped_download_url_skips_the_graph_hop()
    {
        _cdnHandler.SetDefaultResponse(HttpStatusCode.OK, "0123456789");
        var handler = Bytes(Payload);
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Get-MgxContent"),
            new[] { new Hashtable { ["@microsoft.graph.downloadUrl"] = CdnUrl } });

        Assert.Equal(Payload, SingleByteArray(result));
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(1, _cdnHandler.RequestCount);
    }

    [Fact]
    public void A_download_url_off_the_allowlist_is_refused_before_any_fetch()
    {
        var handler = Bytes(Payload);
        using var host = new MgxTestHost(handler);

        var result = host.Run(
            ps => ps.AddCommand("Get-MgxContent"),
            new[] { new Hashtable { ["@microsoft.graph.downloadUrl"] = "https://evil.example.com/file" } });

        Assert.Empty(result.Output);
        Assert.Equal(0, _cdnHandler.RequestCount);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("ContentDownloadRefused", error.FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_over_the_pipeline_guard_is_refused_before_it_is_read()
    {
        var handler = new StubHttpMessageHandler().Enqueue(_ =>
        {
            var content = new StreamContent(new MemoryStream(Payload));
            content.Headers.ContentLength = GetMgxContent.MaxPipelineBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/1/content"));

        Assert.Empty(result.Output);
        Assert.NotNull(result.Terminating);
        Assert.Equal("ContentTooLargeForPipeline", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_graph_error_surfaces_without_killing_the_pipeline()
    {
        var handler = new StubHttpMessageHandler().EnqueueJson(HttpStatusCode.NotFound,
            """{ "error": { "code": "itemNotFound", "message": "Item not found." } }""");
        using var host = new MgxTestHost(handler);

        var result = host.Run(ps => ps.AddCommand("Get-MgxContent")
            .AddParameter("Uri", "/me/drive/items/missing/content"));

        Assert.Empty(result.Output);
        Assert.Null(result.Terminating);
        Assert.NotEmpty(result.Errors);
    }

    private static Hashtable DriveItem(string id, string driveId = "drive-1") => new()
    {
        ["id"] = id,
        ["parentReference"] = new Hashtable { ["driveId"] = driveId }
    };
}
