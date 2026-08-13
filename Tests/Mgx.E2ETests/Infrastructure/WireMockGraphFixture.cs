using System.Net.Security;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Mgx.E2ETests.Infrastructure;

/// <summary>
/// One WireMock container for the whole assembly, serving canned Graph responses over HTTPS.
/// HTTPS is mandatory rather than cosmetic, because NextLinkValidator rejects any nextLink that is
/// not https and a plain-HTTP container would make every paging test stop after page one and still
/// report success.
/// </summary>
public sealed class WireMockGraphFixture : IAsyncLifetime
{
    private const string Image = "wiremock/wiremock:3.13.1";
    private const int AdminPort = 8080;
    private const int GraphPort = 8443;

    private IContainer? _container;
    private HttpClient? _admin;

    /// <summary>False when no Docker daemon answered, which makes every E2E test skip.</summary>
    public static bool DockerAvailable { get; private set; }

    public static string? StartupError { get; private set; }

    /// <summary>Value for <c>MgxCmdletBase.s_graphEndpoint</c>, e.g. https://localhost:49155.</summary>
    public string GraphEndpoint { get; private set; } = string.Empty;

    /// <summary>The HttpClient handed to the cmdlets through the test transport seam.</summary>
    public HttpClient Transport { get; private set; } = default!;

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder()
                .WithImage(Image)
                .WithPortBinding(AdminPort, assignRandomHostPort: true)
                .WithPortBinding(GraphPort, assignRandomHostPort: true)
                .WithCommand("--https-port", GraphPort.ToString(), "--disable-banner")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                    r => r.ForPort(AdminPort).ForPath("/__admin/mappings")))
                .Build();

            await _container.StartAsync();
            DockerAvailable = true;
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            StartupError = $"{ex.GetType().Name}: {ex.Message}";
            return;
        }

        var host = _container.Hostname;
        GraphEndpoint = $"https://{host}:{_container.GetMappedPublicPort(GraphPort)}";

        // Trusts WireMock's self-signed certificate
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            }
        };
        Transport = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _admin = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{_container.GetMappedPublicPort(AdminPort)}/__admin/")
        };
    }

    /// <summary>Clears all stubs, scenario state and the request journal.</summary>
    public async Task ResetAsync()
    {
        using var response = await _admin!.PostAsync("reset", content: null);
        response.EnsureSuccessStatusCode();
    }

    public async Task StubAsync(string mappingJson)
    {
        using var content = new StringContent(mappingJson, Encoding.UTF8, "application/json");
        using var response = await _admin!.PostAsync("mappings", content);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"WireMock rejected a stub ({(int)response.StatusCode}): "
                + $"{await response.Content.ReadAsStringAsync()}\n{mappingJson}");
        }
    }

    /// <summary>Everything the cmdlets actually put on the wire.</summary>
    public async Task<JsonDocument> JournalAsync()
    {
        using var response = await _admin!.GetAsync("requests");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    /// <summary>Guards against a run that passed only because pagination stopped early.</summary>
    public async Task<int> RequestCountAsync()
    {
        using var journal = await JournalAsync();
        return journal.RootElement.GetProperty("requests").GetArrayLength();
    }

    public async ValueTask DisposeAsync()
    {
        Transport?.Dispose();
        _admin?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }
}
