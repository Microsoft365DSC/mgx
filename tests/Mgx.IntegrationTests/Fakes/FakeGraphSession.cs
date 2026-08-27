using System.Reflection;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests.Fakes;

/// <summary>
/// The AuthContext shape MgxCmdletBase fingerprints. Only the members it reads are present.
/// </summary>
public sealed class FakeAuthContext
{
    public string? TenantId { get; set; } = "11111111-1111-1111-1111-111111111111";
    public string? ClientId { get; set; } = "22222222-2222-2222-2222-222222222222";
    public string? AuthType { get; set; } = "Delegated";
    public string? Account { get; set; } = "user@contoso.com";
    public string? Authority { get; set; } = "https://login.microsoftonline.com/common";
    public string[]? Scopes { get; set; } = ["User.Read.All"];
}

public sealed class FakeGraphEnvironment
{
    public string? GraphEndpoint { get; set; } = "https://graph.microsoft.com";
    public string? AzureADEndpoint { get; set; } = "https://login.microsoftonline.com";
}

/// <summary>
/// Stands in for Microsoft.Graph.PowerShell.Authentication.GraphSession, found through
/// <see cref="GraphSdkStub"/> rather than by name.
/// </summary>
public sealed class FakeGraphSession
{
    public static FakeGraphSession? Instance { get; set; }

    public object? AuthContext { get; set; } = new FakeAuthContext();

    public object? Environment { get; set; } = new FakeGraphEnvironment();

    public HttpClient? GraphHttpClient { get; set; }
}

/// <summary>
/// Stands in for the SDK's AuthenticationHelpers, whose provider the bridge only passes on to
/// the handler constructor.
/// </summary>
public static class FakeAuthenticationHelpers
{
    public static Task<object> GetAuthenticationProviderAsync(object authContext) =>
        Task.FromResult<object>(new object());
}

/// <summary>
/// Stands in for the SDK's AuthenticationHandler, answering rather than delegating to keep the
/// built client off the network.
/// </summary>
public sealed class FakeAuthenticationHandler : DelegatingHandler
{
    public FakeAuthenticationHandler(object authProvider, HttpMessageHandler inner)
    {
        AuthProvider = authProvider;
        InnerHandler = inner;
    }

    public object AuthProvider { get; }

    /// <summary>Requests the built client actually sent, in order.</summary>
    public static List<string> Sent { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add(request.RequestUri?.ToString() ?? string.Empty);
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{ \"value\": [] }", System.Text.Encoding.UTF8, "application/json")
        });
    }
}

/// <summary>
/// Installs the fake Graph SDK types behind MgxCmdletBase's type resolver for the life of the
/// instance. Names it does not fake fall through to the real assemblies in the process.
/// </summary>
public sealed class GraphSdkStub : IDisposable
{
    private const string SessionType = "Microsoft.Graph.PowerShell.Authentication.GraphSession";
    private const string HelpersType =
        "Microsoft.Graph.PowerShell.Authentication.Core.Utilities.AuthenticationHelpers";
    private const string HandlerType =
        "Microsoft.Graph.PowerShell.Authentication.Handlers.AuthenticationHandler";

    private readonly bool _withAuthHelpers;
    private readonly bool _sdkPresent;

    public GraphSdkStub(bool connected = true, bool withAuthHelpers = true, bool sdkPresent = true)
    {
        _withAuthHelpers = withAuthHelpers;
        _sdkPresent = sdkPresent;
        Session = new FakeGraphSession();
        if (!connected)
            Session.AuthContext = null;
        FakeGraphSession.Instance = sdkPresent ? Session : null;
        MgxCmdletBase.s_typeResolverForTests = Resolve;
    }

    /// <summary>A process with no Graph SDK in it at all.</summary>
    public static GraphSdkStub WithoutTheSdk() => new(sdkPresent: false);

    public FakeGraphSession Session { get; }

    /// <summary>Drops GraphSession.Instance while the fake types stay resolvable.</summary>
    public void ClearInstance() => FakeGraphSession.Instance = null;

    private Type? Resolve(string fullName)
    {
        if (!_sdkPresent && (fullName == SessionType || fullName == HelpersType || fullName == HandlerType))
            return null;

        return fullName switch
        {
            SessionType => typeof(FakeGraphSession),
            HelpersType => _withAuthHelpers ? typeof(FakeAuthenticationHelpers) : null,
            HandlerType => _withAuthHelpers ? typeof(FakeAuthenticationHandler) : null,
            _ => RealType(fullName)
        };
    }

    private static Type? RealType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException) { return []; }
            })
            .FirstOrDefault(t => t.FullName == fullName);

    public void Dispose()
    {
        MgxCmdletBase.s_typeResolverForTests = null;
        FakeGraphSession.Instance = null;
        lock (FakeAuthenticationHandler.Sent) FakeAuthenticationHandler.Sent.Clear();
    }
}
