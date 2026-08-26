using System;
using System.IO;
using System.Management.Automation;
using System.Reflection;
using System.Runtime.Loader;
using Mgx.Cmdlets;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for AlcInitializer.
/// </summary>
// Touches MgxCmdletBase and pipeline statics, so it must not run beside the injected-mock tests
[Collection("Pipeline")]
public class AlcInitializerCoverageTests
{
    private static readonly string DepsPath = Path.Combine(
        Path.GetDirectoryName(typeof(AlcInitializer).Assembly.Location)!,
        "Dependencies");

    [Fact]
    public void ResolveDependency_ReturnsNull_ForNonExistentAssembly()
    {
        var name = new AssemblyName("NonExistentAssembly.Test");
        var alc = AssemblyLoadContext.Default;

        var result = AlcInitializer.ResolveDependency(alc, name);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDependency_ReturnsLoadedAssembly_WhenAlreadyLoaded()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var name = thisAssembly.GetName();

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name);

        Assert.NotNull(result);
        Assert.Equal(thisAssembly.GetName().Name, result.GetName().Name);
    }

    [Fact]
    public void ResolveDependency_ReturnsNull_WhenIncompatibleMajorVersion()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var loadedName = thisAssembly.GetName();
        var name = new AssemblyName(loadedName.Name + ", Version=999.0.0.0");

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDependency_ReturnsNull_ForMalformedAssemblyName()
    {
        var name = new AssemblyName("Invalid/Name");
        var alc = AssemblyLoadContext.Default;

        var result = AlcInitializer.ResolveDependency(alc, name);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDependency_VersionCheck_SameMajorHigherMinor_ReturnsLoaded()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var loadedName = thisAssembly.GetName();
        var version = loadedName.Version!;
        var name = new AssemblyName($"{loadedName.Name}, Version={version.Major}.{Math.Max(0, version.Minor - 1)}.0.0");
        
        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name);
        
        Assert.NotNull(result);
    }

    [Fact]
    public void ResolveDependency_NullVersion_ReturnsLoaded()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var loadedName = thisAssembly.GetName();
        var name = new AssemblyName(loadedName.Name ?? string.Empty) { Version = null };
        
        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name);
        
        Assert.NotNull(result);
    }
}
