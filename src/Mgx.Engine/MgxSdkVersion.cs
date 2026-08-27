using System.Reflection;

namespace Mgx.Engine;

/// <summary>
/// SDK version identifier injected into the SdkVersion HTTP header on all Graph requests.
/// Enables correlation of Mgx traffic in Microsoft's Graph API telemetry.
/// </summary>
internal static class MgxSdkVersion
{
    /// <summary>Header value, e.g. "mgx/2.1.0".</summary>
    internal static readonly string Value = $"mgx/{Read()}";

    private static string Read()
    {
        var assembly = typeof(MgxSdkVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
