namespace Mgx.Engine;

/// <summary>
/// SDK version identifier injected into the SdkVersion HTTP header on all Graph requests.
/// Enables correlation of Mgx traffic in Microsoft's Graph API telemetry.
/// <para>
/// This MUST match ModuleVersion in module/mgx.psd1, which is the single source of truth for the
/// module's version. It is a hand-maintained constant only because neither project sets an
/// assembly &lt;Version&gt;, and adding one would create a third place to keep in sync rather than
/// fewer. build.ps1 fails the build if this string and the manifest disagree - it silently
/// reported 0.3.0 for three releases before that gate existed.
/// </para>
/// </summary>
internal static class MgxSdkVersion
{
    internal const string Value = "mgx/1.0.3";
}
