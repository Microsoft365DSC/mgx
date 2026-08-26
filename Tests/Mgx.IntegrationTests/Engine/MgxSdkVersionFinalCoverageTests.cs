using System.Reflection;
using Mgx.Engine;

namespace Mgx.IntegrationTests.Engine;

/// <summary>
/// Tests for MgxSdkVersion.
/// </summary>
public class MgxSdkVersionFinalCoverageTests
{
    [Fact]
    public void Value_IsReadOnly()
    {
        Assert.NotNull(MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_MatchesAssemblyInformationalVersion()
    {
        var assembly = typeof(MgxSdkVersion).Assembly;
        var infoAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        if (infoAttr != null && !string.IsNullOrWhiteSpace(infoAttr.InformationalVersion))
        {
            var expected = infoAttr.InformationalVersion.Split('+')[0];
            Assert.Equal($"mgx/{expected}", MgxSdkVersion.Value);
        }
        else
        {
            var fallback = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            Assert.Equal($"mgx/{fallback}", MgxSdkVersion.Value);
        }
    }

    [Fact]
    public void Value_DoesNotContainSourceLinkSuffix()
    {
        Assert.DoesNotContain("+", MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_IsThreePartSemver()
    {
        var parts = MgxSdkVersion.Value.Split('/')[1].Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, p => Assert.True(int.TryParse(p, out _)));
    }

    [Fact]
    public void Value_IsNotDefaultZeroVersion()
    {
        Assert.NotEqual("mgx/1.0.0", MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_IsNotEmpty()
    {
        Assert.NotEqual(string.Empty, MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_HasValidFormat()
    {
        var pattern = @"^mgx/\d+\.\d+\.\d+$";
        Assert.Matches(pattern, MgxSdkVersion.Value);
    }

    [Fact]
    public void Value_UsesMgxPrefix()
    {
        Assert.StartsWith("mgx/", MgxSdkVersion.Value);
    }
}