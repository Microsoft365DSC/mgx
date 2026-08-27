using System.Net;
using System.Reflection;
using System.Text;
using Mgx.Cmdlets.Base;
using Mgx.Cmdlets.Cmdlets.Configuration;
using Mgx.Engine.Http;
using Mgx.IntegrationTests.Engine;
using Mgx.IntegrationTests.Fakes;
using Mgx.IntegrationTests.Infrastructure;

namespace Mgx.IntegrationTests;

/// <summary>
/// The reflection bridge into the Graph SDK, driven through a stubbed GraphSession.
/// </summary>
[Collection(ResilienceCollection.Name)]
public class GraphSdkBridgeTests : IDisposable
{
    private readonly string? _modulePath = Environment.GetEnvironmentVariable("PSModulePath");

    public GraphSdkBridgeTests()
    {
        // Enable-MgxResilience probes with Invoke-MgGraphRequest, which would auto-load a real
        // Microsoft.Graph.Authentication from the developer machine and leave its GraphSession
        // resolvable for every later test in the process
        Environment.SetEnvironmentVariable("PSModulePath", string.Empty);
        ResetStatics();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PSModulePath", _modulePath);
        ResetStatics();
        GC.SuppressFinalize(this);
    }

    private static void ResetStatics()
    {
        MgxCmdletBase.s_typeResolverForTests = null;
        EnableMgxResilience.IsEnabled = false;
        EnableMgxResilience.OriginalSdkClient = null;
        EnableMgxResilience.ResilientSdkClient = null;
        EnableMgxResilience.ActiveHandler = null;
        MgxCmdletBase.ResetHttpClient();
    }

    private static HttpClient? BuiltClient() => (HttpClient?)typeof(MgxCmdletBase)
        .GetField("s_graphHttpClient", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);

    private static StubHttpMessageHandler Ok() =>
        new StubHttpMessageHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{ "id": "u1" }""", Encoding.UTF8, "application/json")
        });

    // --- identity ---

    [Fact]
    public void A_connected_session_produces_a_stable_fingerprint()
    {
        using var stub = new GraphSdkStub();

        var first = MgxCmdletBase.BuildAuthFingerprint(stub.Session.AuthContext, "https://graph.microsoft.com");
        var second = MgxCmdletBase.BuildAuthFingerprint(stub.Session.AuthContext, "https://graph.microsoft.com");

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_different_tenant_produces_a_different_fingerprint()
    {
        using var stub = new GraphSdkStub();
        var context = (FakeAuthContext)stub.Session.AuthContext!;
        var before = MgxCmdletBase.BuildAuthFingerprint(context, null);

        context.TenantId = "33333333-3333-3333-3333-333333333333";

        Assert.NotEqual(before, MgxCmdletBase.BuildAuthFingerprint(context, null));
    }

    [Fact]
    public void Scope_order_does_not_change_the_fingerprint()
    {
        var a = new FakeAuthContext { Scopes = ["User.Read.All", "Group.Read.All"] };
        var b = new FakeAuthContext { Scopes = ["Group.Read.All", "User.Read.All"] };

        Assert.Equal(MgxCmdletBase.BuildAuthFingerprint(a, null), MgxCmdletBase.BuildAuthFingerprint(b, null));
    }

    [Fact]
    public void The_session_endpoint_is_what_the_module_targets()
    {
        using var stub = new GraphSdkStub();
        ((FakeGraphEnvironment)stub.Session.Environment!).GraphEndpoint = "https://graph.microsoft.us";

        Assert.Equal("https://graph.microsoft.us", MgxCmdletBase.GetGraphEndpoint(null, null));
    }

    [Fact]
    public void A_session_without_an_instance_reports_no_endpoint()
    {
        using var stub = new GraphSdkStub();
        stub.ClearInstance();

        Assert.Null(MgxCmdletBase.GetGraphEndpoint(null, null));
    }

    // --- client build ---

    [Fact]
    public void A_connected_session_gets_an_auth_only_client_of_its_own()
    {
        using var stub = new GraphSdkStub();
        var warnings = new List<string>();

        MgxCmdletBase.TryPreInitHttpClient(warnings.Add, _ => { });

        Assert.NotNull(BuiltClient());
        Assert.Empty(warnings);
    }

    [Fact]
    public void Without_the_sdk_auth_helpers_no_client_is_built()
    {
        using var stub = new GraphSdkStub(withAuthHelpers: false);

        MgxCmdletBase.TryPreInitHttpClient(_ => { }, _ => { });

        Assert.Null(BuiltClient());
    }

    [Fact]
    public void A_disconnected_session_builds_nothing()
    {
        using var stub = new GraphSdkStub(connected: false);

        MgxCmdletBase.TryPreInitHttpClient(_ => { }, _ => { });

        Assert.Null(BuiltClient());
    }

    // --- Enable / Disable round trip ---

    [Fact]
    public void Enable_wraps_the_sdk_client_and_Disable_puts_the_original_back()
    {
        using var stub = new GraphSdkStub();
        var original = new HttpClient { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
        stub.Session.GraphHttpClient = original;
        using var host = new MgxTestHost(Ok());

        var enable = host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        Assert.Null(enable.Terminating);
        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.NotSame(original, stub.Session.GraphHttpClient);
        Assert.Same(stub.Session.GraphHttpClient, EnableMgxResilience.ResilientSdkClient);
        Assert.Equal(original.BaseAddress, stub.Session.GraphHttpClient!.BaseAddress);

        var disable = host.Run(ps => ps.AddCommand("Disable-MgxResilience"));

        Assert.Null(disable.Terminating);
        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Same(original, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void Enabling_twice_leaves_the_first_wrap_in_place()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());

        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));
        var wrapped = stub.Session.GraphHttpClient;
        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        Assert.Same(wrapped, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void Enable_under_WhatIf_leaves_the_sdk_client_alone()
    {
        using var stub = new GraphSdkStub();
        var original = new HttpClient();
        stub.Session.GraphHttpClient = original;
        using var host = new MgxTestHost(Ok());

        host.Run(ps => ps.AddCommand("Enable-MgxResilience").AddParameter("WhatIf"));

        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Same(original, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void Enable_without_a_graph_session_says_so()
    {
        using var stub = GraphSdkStub.WithoutTheSdk();
        using var host = new MgxTestHost(Ok());

        var result = host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        Assert.NotNull(result.Terminating);
        Assert.Equal("GraphSessionNotFound", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void Enable_with_the_session_torn_down_says_so()
    {
        using var stub = new GraphSdkStub();
        stub.ClearInstance();
        using var host = new MgxTestHost(Ok());

        var result = host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        Assert.NotNull(result.Terminating);
        Assert.Equal("GraphSessionNull", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void Enable_with_no_sdk_client_to_wrap_says_so()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = null;
        using var host = new MgxTestHost(Ok());

        var result = host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        Assert.NotNull(result.Terminating);
        Assert.Equal("HttpClientNotFound", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void Disable_before_Enable_only_warns()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());

        var result = host.Run(ps => ps.AddCommand("Disable-MgxResilience"));

        Assert.Null(result.Terminating);
        Assert.Contains(result.Warnings, w => w.Contains("not currently enabled"));
    }

    [Fact]
    public void Disable_under_WhatIf_leaves_the_wrap_installed()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());

        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));
        var wrapped = stub.Session.GraphHttpClient;

        host.Run(ps => ps.AddCommand("Disable-MgxResilience").AddParameter("WhatIf"));

        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.Same(wrapped, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void Disable_restores_the_original_even_when_something_else_swapped_the_client()
    {
        using var stub = new GraphSdkStub();
        var original = new HttpClient();
        stub.Session.GraphHttpClient = original;
        using var host = new MgxTestHost(Ok());

        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));
        var stranger = new HttpClient();
        stub.Session.GraphHttpClient = stranger;

        var result = host.Run(ps => ps.AddCommand("Disable-MgxResilience"));

        Assert.Contains(result.Warnings, w => w.Contains("not the one injected"));
        Assert.Same(original, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void Get_MgxResilience_reports_the_wrap_once_it_is_installed()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());

        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));
        var result = host.Run(ps => ps.AddCommand("Get-MgxResilience"));

        Assert.Equal(true, Assert.Single(result.Output).Properties["IsEnabled"].Value);
    }

    // --- identity change ---

    [Fact]
    public void A_new_identity_re_injects_the_wrap_against_the_new_sdk_client()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());
        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));
        var firstWrap = EnableMgxResilience.ResilientSdkClient;

        // Connect-MgGraph replaces the SDK client along with the credential
        var reconnected = new HttpClient();
        stub.Session.GraphHttpClient = reconnected;
        var warnings = new List<string>();
        var verbose = new List<string>();

        EnableMgxResilience.RefreshInjectedClient(warnings.Add, verbose.Add);

        Assert.True(EnableMgxResilience.IsEnabled);
        Assert.NotSame(firstWrap, EnableMgxResilience.ResilientSdkClient);
        Assert.Same(reconnected, EnableMgxResilience.OriginalSdkClient);
        Assert.Same(EnableMgxResilience.ResilientSdkClient, stub.Session.GraphHttpClient);
    }

    [Fact]
    public void A_new_identity_that_kept_our_wrap_drops_it_and_asks_for_a_re_enable()
    {
        using var stub = new GraphSdkStub();
        stub.Session.GraphHttpClient = new HttpClient();
        using var host = new MgxTestHost(Ok());
        host.Run(ps => ps.AddCommand("Enable-MgxResilience"));

        var warnings = new List<string>();
        EnableMgxResilience.RefreshInjectedClient(warnings.Add, _ => { });

        Assert.False(EnableMgxResilience.IsEnabled);
        Assert.Null(stub.Session.GraphHttpClient);
        Assert.Contains(warnings, w => w.Contains("run Enable-MgxResilience again"));
    }

    // --- the client the cmdlets actually run on ---

    private static MgxTestHost SdkBackedHost(ResilientGraphClientOptions? options = null) =>
        new(Ok(), "https://graph.microsoft.com", options, useTestTransport: false);

    [Fact]
    public void A_request_runs_on_the_client_built_from_the_session()
    {
        using var stub = new GraphSdkStub();
        using var host = SdkBackedHost();

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Null(result.Terminating);
        Assert.Equal("https://graph.microsoft.com/v1.0/users",
            Assert.Single(FakeAuthenticationHandler.Sent));
        Assert.NotNull(BuiltClient());
    }

    [Fact]
    public void A_sovereign_endpoint_on_the_session_is_what_the_request_targets()
    {
        using var stub = new GraphSdkStub();
        ((FakeGraphEnvironment)stub.Session.Environment!).GraphEndpoint = "https://graph.microsoft.us";
        using var host = SdkBackedHost();

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.StartsWith("https://graph.microsoft.us/", Assert.Single(FakeAuthenticationHandler.Sent),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_request_on_the_same_identity_reuses_the_client()
    {
        using var stub = new GraphSdkStub();
        using var host = SdkBackedHost();

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));
        var first = BuiltClient();
        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/groups"));

        Assert.Same(first, BuiltClient());
    }

    [Fact]
    public void A_reconnect_under_a_different_tenant_rebuilds_the_client()
    {
        using var stub = new GraphSdkStub();
        using var host = SdkBackedHost();

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));
        var first = BuiltClient();

        stub.Session.AuthContext = new FakeAuthContext { TenantId = "44444444-4444-4444-4444-444444444444" };
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Verbose"));

        Assert.NotSame(first, BuiltClient());
        Assert.Contains(result.Verbose, v => v.Contains("Graph identity changed"));
    }

    [Fact]
    public void A_new_total_timeout_rebuilds_the_client_it_is_baked_into()
    {
        using var stub = new GraphSdkStub();
        using var host = SdkBackedHost();

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));
        var first = BuiltClient();

        host.Run(ps => ps.AddCommand("Set-MgxOption").AddParameter("TotalTimeoutSeconds", 45));
        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest")
            .AddParameter("Uri", "/users")
            .AddParameter("Verbose"));

        Assert.NotSame(first, BuiltClient());
        Assert.Equal(TimeSpan.FromSeconds(105), BuiltClient()!.Timeout);
        Assert.Contains(result.Verbose, v => v.Contains("TotalTimeoutSeconds changed"));
    }

    [Fact]
    public void A_disconnected_session_stops_the_cmdlet_before_any_request()
    {
        using var stub = new GraphSdkStub(connected: false);
        using var host = SdkBackedHost();

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.NotNull(result.Terminating);
        Assert.Equal("NotConnected", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
        Assert.Empty(FakeAuthenticationHandler.Sent);
    }

    [Fact]
    public void No_graph_module_at_all_names_the_install_step_instead_of_the_connect_step()
    {
        using var stub = GraphSdkStub.WithoutTheSdk();
        using var host = SdkBackedHost();

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Equal("GraphAuthModuleNotLoaded", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_session_the_module_cannot_build_a_client_from_falls_back_and_then_gives_up()
    {
        using var stub = new GraphSdkStub(withAuthHelpers: false);
        using var host = SdkBackedHost();

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Contains(result.Warnings, w => w.Contains("Falling back to SDK client"));
        Assert.Equal("HttpClientInitFailed", result.Terminating!.FullyQualifiedErrorId.Split(',')[0]);
    }

    [Fact]
    public void A_session_the_module_cannot_build_from_borrows_the_sdk_client_when_there_is_one()
    {
        using var stub = new GraphSdkStub(withAuthHelpers: false);
        var borrowed = new HttpClient(new FakeAuthenticationHandler(new object(), new HttpClientHandler()));
        stub.Session.GraphHttpClient = borrowed;
        using var host = SdkBackedHost();

        var result = host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Null(result.Terminating);
        Assert.Same(borrowed, BuiltClient());
    }

    [Fact]
    public void The_sdk_client_going_stale_under_a_borrowed_transport_rebuilds_it()
    {
        using var stub = new GraphSdkStub(withAuthHelpers: false);
        stub.Session.GraphHttpClient =
            new HttpClient(new FakeAuthenticationHandler(new object(), new HttpClientHandler()));
        using var host = SdkBackedHost();

        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        var replaced = new HttpClient(new FakeAuthenticationHandler(new object(), new HttpClientHandler()));
        stub.Session.GraphHttpClient = replaced;
        host.Run(ps => ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users"));

        Assert.Same(replaced, BuiltClient());
    }

    [Fact]
    public void The_sdk_retry_handler_is_disarmed_inside_the_wrap()
    {
        // The resolver only sees assemblies the process has already loaded
        _ = new Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options.RetryHandlerOption();
        using var stub = new GraphSdkStub();

        var options = EnableMgxResilience.BuildInnerRetryOverride();

        var option = Assert.Single(options!).Value;
        Assert.NotNull(option);
        Assert.Equal(0, option!.GetType().GetProperty("MaxRetry")!.GetValue(option));
    }
}
