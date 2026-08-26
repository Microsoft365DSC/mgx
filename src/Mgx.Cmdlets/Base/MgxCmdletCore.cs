using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mgx.Cmdlets.Base;

/// <summary>
/// Protocol-neutral base for Mgx cmdlets. Owns cancellation, disposal, and JSON-to-Hashtable
/// conversion — everything that is not tied to a specific transport.
/// <para>
/// Graph cmdlets derive from <see cref="MgxCmdletBase"/>, which adds the Graph HTTP client and
/// auth on top of this. Keeping the two apart means a cmdlet needing only the lifecycle and
/// conversion helpers does not also inherit the static HttpClient state or the
/// Connect-MgGraph requirement.
/// </para>
/// </summary>
public abstract class MgxCmdletCore : PSCmdlet, IDisposable
{
    private CancellationTokenSource _cts = new();
    private readonly CancellationToken _token;
    private int _disposed; // 0 = not disposed, 1 = disposed (Interlocked for thread safety)

    // DateTime parsing requires a YYYY-MM-DDT prefix, which keeps version strings, GUIDs and
    // numeric ids from matching
    private static readonly Regex Iso8601Pattern = new(
        @"^\d{4}-\d{2}-\d{2}[T ]", RegexOptions.Compiled);

    /// <summary>The cancellation token for this invocation.</summary>
    /// <remarks>
    /// A cached copy rather than <c>_cts.Token</c>. StopProcessing cancels and then disposes the
    /// source, and reading <c>Token</c> on a disposed source is documented to throw. Catch blocks
    /// unwinding a Ctrl-C read this before deciding whether to promote or delete a temp file, so
    /// a throw there would skip the cleanup and orphan a partial file. A token struct copied
    /// before disposal stays usable.
    /// </remarks>
    protected CancellationToken CancellationToken => _token;

    #region Lifecycle

    protected MgxCmdletCore()
    {
        // Copy the token once, while the source is guaranteed alive.
        _token = _cts.Token;
    }

    protected override void StopProcessing()
    {
        _cts.Cancel();
        Dispose();
    }

    protected override void EndProcessing()
    {
        Dispose();
    }

    /// <summary>
    /// Subclass hook for releasing transport-specific resources (the Graph HTTP client).
    /// Called exactly once, inside the same Interlocked guard that protects <see cref="Dispose"/>.
    /// </summary>
    protected virtual void DisposeCore() { }

    public void Dispose()
    {
        // StopProcessing and EndProcessing run on different threads and can race, so Interlocked
        // lets only one enter the dispose body
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
        {
            _cts.Cancel();
            _cts.Dispose();
            DisposeCore();
        }
        GC.SuppressFinalize(this);
    }

    #endregion

    #region JSON conversion

    /// <summary>Convert a JsonElement to a case-insensitive Hashtable with all properties preserved.</summary>
    protected internal static Hashtable JsonToHashtable(JsonElement element)
    {
        // OrdinalIgnoreCase matches PowerShell's @{} literal, so member access stays
        // case-insensitive ($user.DisplayName resolves the camelCase 'displayName' key).
        var ht = new Hashtable(StringComparer.OrdinalIgnoreCase);

        // Non-Object elements (string, number, etc.) must wrap value in a property
        if (element.ValueKind != JsonValueKind.Object)
        {
            ht["Value"] = ConvertJsonValue(element);
            return ht;
        }

        foreach (var prop in element.EnumerateObject())
        {
            // Strip @odata.* transport metadata but keep @odata.type verbatim, since it
            // round-trips on write and drives polymorphic handling.
            // @odata.etag changes on every write, so keeping it would make two reads of an
            // unchanged entity compare unequal and show as drift in Microsoft365DSC. Callers
            // needing the If-Match tag read it from the raw payload
            if (prop.Name.StartsWith("@odata.", StringComparison.OrdinalIgnoreCase)
                && !prop.Name.Equals("@odata.type", StringComparison.OrdinalIgnoreCase))
                continue;

            // Indexer, not Add: keys differing only by case would throw with Add
            ht[prop.Name] = ConvertJsonValue(prop.Value);
        }

        return ht;
    }

    private static object? ConvertJsonValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var str = element.GetString();
            if (str != null && Iso8601Pattern.IsMatch(str) &&
                DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dto))
                return dto.UtcDateTime;
            return str;
        }

        return element.ValueKind switch
        {
            // The (object) cast is required, or the conditional unifies to double and every
            // integer loses precision beyond 2^53
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.Object ? (object?)JsonToHashtable(item) : ConvertJsonValue(item))
                .ToArray(),
            JsonValueKind.Object => JsonToHashtable(element),
            _ => element.GetRawText()
        };
    }

    #endregion

    #region Pipeline input helpers

    /// <summary>
    /// Unwrap a PSObject to the .NET value underneath. A PSCustomObject is returned as its
    /// PSObject, because its members live there and its BaseObject carries nothing.
    /// </summary>
    protected internal static object UnwrapPSObject(object input) =>
        input is PSObject pso && pso.BaseObject is not PSObject and not PSCustomObject
            ? pso.BaseObject
            : input;

    /// <summary>
    /// Read a named member from pipeline input, whether it is a Hashtable, a
    /// PSObject-wrapped dictionary, or a PSCustomObject.
    /// </summary>
    protected internal static object? TryGetMember(object? input, string name)
    {
        if (input is PSObject wrapper && wrapper.BaseObject is IDictionary baseDict)
            return baseDict[name];
        if (input is IDictionary dict)
            return dict[name];
        if (input is PSObject pso)
            return pso.Properties[name]?.Value;
        return null;
    }

    #endregion
}
