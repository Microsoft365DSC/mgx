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
public class AlcInitializerCoverageTests : IDisposable
{
    // Stands in for Dependencies/. Placeholder files are empty, the resolver only checks they exist
    private readonly string _depsPath = Directory.CreateTempSubdirectory("mgx-deps-").FullName;

    public void Dispose() => Directory.Delete(_depsPath, recursive: true);

    private void CarryInDependencies(string assemblyName) =>
        File.WriteAllBytes(Path.Combine(_depsPath, $"{assemblyName}.dll"), []);

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
        CarryInDependencies(name.Name!);

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name, _depsPath);

        Assert.NotNull(result);
        Assert.Equal(thisAssembly.GetName().Name, result.GetName().Name);
    }

    [Fact]
    public void ResolveDependency_ReturnsNull_ForLoadedAssemblyNotCarriedInDependencies()
    {
        // Loaded, but absent from Dependencies/
        var name = typeof(AlcInitializer).Assembly.GetName();

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name, _depsPath);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveDependency_ReturnsNull_WhenIncompatibleMajorVersion()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var loadedName = thisAssembly.GetName();
        CarryInDependencies(loadedName.Name!);
        var name = new AssemblyName(loadedName.Name + ", Version=999.0.0.0");

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name, _depsPath);

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
        CarryInDependencies(loadedName.Name!);
        var version = loadedName.Version!;
        var name = new AssemblyName($"{loadedName.Name}, Version={version.Major}.{Math.Max(0, version.Minor - 1)}.0.0");

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name, _depsPath);

        Assert.NotNull(result);
    }

    [Fact]
    public void ResolveDependency_NullVersion_ReturnsLoaded()
    {
        var thisAssembly = typeof(AlcInitializer).Assembly;
        var loadedName = thisAssembly.GetName();
        CarryInDependencies(loadedName.Name!);
        var name = new AssemblyName(loadedName.Name ?? string.Empty) { Version = null };

        var result = AlcInitializer.ResolveDependency(AssemblyLoadContext.Default, name, _depsPath);

        Assert.NotNull(result);
    }
}
