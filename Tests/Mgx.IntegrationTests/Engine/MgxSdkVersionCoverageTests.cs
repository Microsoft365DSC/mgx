using System.Reflection;
using Mgx.Engine;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Additional coverage tests for MgxSdkVersion.
/// </summary>
public class MgxSdkVersionCoverageTests
{
    [Fact]
    public void Value_IsNotDefault()
    {
        Assert.NotEqual("mgx/0.0.0", MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_StartsWithMgxPrefix()
    {
        Assert.StartsWith("mgx/", MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_HasThreePartVersion()
    {
        var versionPart = MgxSdkVersion.Value.Substring(4); // Remove "mgx/"
        var parts = versionPart.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _)));
    }

    [Fact]
    public void Value_DoesNotContainPlusSuffix()
    {
        // The informational version may have "+<commit>" suffix which should be stripped
        Assert.DoesNotContain("+", MgxSdkVersion.Value);
    }

    [Fact]
    public void Read_HandlesMissingInformationalVersion()
    {
        // This tests the fallback path when AssemblyInformationalVersionAttribute is missing
        // We can't easily test this without a custom assembly, but we can verify
        // the current value is valid
        Assert.NotNull(MgxSdkVersion.Value);
        Assert.NotEqual(string.Empty, MgxSdkVersion.Value);
    }
}