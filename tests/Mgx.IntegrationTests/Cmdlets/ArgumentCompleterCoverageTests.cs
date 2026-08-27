using System;
using System.Collections;
using System.Collections.Generic;
using System.Management.Automation;
using System.Management.Automation.Language;
using Mgx.Cmdlets.Base;

#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// Tests for Argument Completers.
/// </summary>
public class ArgumentCompleterCoverageTests
{
    [Fact]
    public void ApiVersionCompleter_ProvidesVersions()
    {
        var completer = new ApiVersionCompleter();
        var results = completer.CompleteArgument("test", "ApiVersion", "v", null, new Hashtable());

        Assert.Contains(results, r => r.CompletionText == "v1.0");
    }

    [Fact]
    public void ApiVersionCompleter_CaseInsensitive()
    {
        var completer = new ApiVersionCompleter();
        var results = completer.CompleteArgument("test", "ApiVersion", "V", null, new Hashtable());

        Assert.Contains(results, r => r.CompletionText == "v1.0");
    }

    [Fact]
    public void ConsistencyLevelCompleter_ProvidesLevels()
    {
        var completer = new ConsistencyLevelCompleter();
        var results = completer.CompleteArgument("test", "ConsistencyLevel", "e", null, new Hashtable());

        Assert.Contains(results, r => r.CompletionText == "eventual");
    }

    [Fact]
    public void ThrottlePriorityCompleter_ProvidesPriorities()
    {
        var completer = new ThrottlePriorityCompleter();
        var results = completer.CompleteArgument("test", "ThrottlePriority", "h", null, new Hashtable());

        Assert.Contains(results, r => r.CompletionText == "High");
    }

    [Fact]
    public void DeltaPreferCompleter_ProvidesPreferTokens()
    {
        var completer = new DeltaPreferCompleter();
        var results = completer.CompleteArgument("test", "Prefer", "delta", null, new Hashtable());

        Assert.Contains(results, r => r.CompletionText == "deltashowremovedasdeleted");
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.ToolTip)));
    }

    [Fact]
    public void DeltaPreferCompleter_OmitsTheStandaloneHeader()
    {
        var completer = new DeltaPreferCompleter();
        var results = completer.CompleteArgument("test", "Prefer", "", null, new Hashtable());

        Assert.DoesNotContain(results, r => r.CompletionText == "deltaExcludeParent");
    }
}