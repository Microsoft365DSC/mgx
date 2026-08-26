using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests.Engine;

public class TransportDefaultsTests
{
    [Fact]
    public void ConnectTimeout_IsTenSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), TransportDefaults.ConnectTimeout);
    }

    [Fact]
    public void PooledConnectionLifetime_IsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), TransportDefaults.PooledConnectionLifetime);
    }

    [Fact]
    public void MaxConnectionsPerServer_IsTwenty()
    {
        Assert.Equal(20, TransportDefaults.MaxConnectionsPerServer);
    }

    [Fact]
    public void EnableMultipleHttp2Connections_IsTrue()
    {
        Assert.True(TransportDefaults.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void Decompression_IncludesGzip()
    {
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.GZip));
    }

    [Fact]
    public void Decompression_IncludesDeflate()
    {
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.Deflate));
    }

    [Fact]
    public void Decompression_IncludesBrotli()
    {
        Assert.True(TransportDefaults.Decompression.HasFlag(DecompressionMethods.Brotli));
    }

    [Fact]
    public void Decompression_IsExactlyThreeMethods()
    {
        var expected = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli;
        Assert.Equal(TransportDefaults.Decompression, expected);
    }
}