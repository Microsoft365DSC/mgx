using System.Net;
using Mgx.Engine.Http;

namespace Mgx.IntegrationTests;

/// <summary>
/// Verifies TransportDefaults constants and that SocketsHttpHandler
/// can be constructed with these values without throwing.
/// </summary>
[Collection("Pipeline")]
public class TransportConfigTests
{
    [Fact]
    public void SocketsHttpHandler_AcceptsTransportDefaults()
    {
        // Verify SocketsHttpHandler can be constructed with all TransportDefaults values.
        // This catches if any constant is out of range for the runtime.
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TransportDefaults.ConnectTimeout,
            PooledConnectionLifetime = TransportDefaults.PooledConnectionLifetime,
            MaxConnectionsPerServer = TransportDefaults.MaxConnectionsPerServer,
            EnableMultipleHttp2Connections = TransportDefaults.EnableMultipleHttp2Connections,
            AutomaticDecompression = TransportDefaults.Decompression
        };

        Assert.Equal(TransportDefaults.ConnectTimeout, handler.ConnectTimeout);
        Assert.Equal(TransportDefaults.PooledConnectionLifetime, handler.PooledConnectionLifetime);
        Assert.Equal(TransportDefaults.MaxConnectionsPerServer, handler.MaxConnectionsPerServer);
        Assert.True(handler.EnableMultipleHttp2Connections);
        Assert.Equal(TransportDefaults.Decompression, handler.AutomaticDecompression);
    }


    [Fact]
    public void TransportDefaults_PinTheShippedValues()
    {
        // These are documented in about_Mgx_Tuning, so a silent change here changes shipped behavior
        Assert.Equal(TimeSpan.FromSeconds(10), TransportDefaults.ConnectTimeout);
        Assert.Equal(TimeSpan.FromMinutes(2), TransportDefaults.PooledConnectionLifetime);
        Assert.Equal(20, TransportDefaults.MaxConnectionsPerServer);
        Assert.True(TransportDefaults.EnableMultipleHttp2Connections);
        Assert.Equal(120, ResilientGraphClientOptions.Default.MaxRetryAfterSeconds);
    }
}
