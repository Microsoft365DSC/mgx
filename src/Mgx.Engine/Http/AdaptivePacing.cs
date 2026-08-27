using System.Diagnostics;

namespace Mgx.Engine.Http;

/// <summary>
/// Coarse workload class for a Graph request URI. Pacing state is partitioned by bucket so a
/// throttle on one service does not slow an unrelated fan-out in the same process. Also decides
/// the delta "sync from now" token form.
/// </summary>
public enum WorkloadBucket
{
    /// <summary>OneDrive and SharePoint content plane.</summary>
    Drive = 0,

    /// <summary>Entra directory objects.</summary>
    Directory = 1,

    /// <summary>Everything else, such as Exchange, Teams, Intune and reports.</summary>
    Other = 2,

    /// <summary>
    /// $batch envelopes. Separate because GraphBatchClient governs batch throughput with its own
    /// item-level AIMD, and a batch throttle says nothing about the other workloads.
    /// </summary>
    Batch = 3
}

/// <summary>
/// Shared AIMD pacing math and the workload classifier, used by both AdaptiveRequestPacer and
/// batch item pacing. Public for the classifier alone, the pacing math stays internal.
/// </summary>
public static class AdaptivePacing
{
    internal const int WorkloadBucketCount = 4;

    /// <summary>Floor for any adapted rate. Halving without a floor would disable pacing.</summary>
    internal const int MinAdaptiveRate = 2;

    /// <summary>
    /// How long a reduced rate persists after the last throttle. Also the quiet period after
    /// which the request pacer treats a workload as cold and re-enters slow start.
    /// </summary>
    internal static readonly TimeSpan AdaptiveRecoveryWindow = TimeSpan.FromMinutes(5);

    /// <summary>Rate to fall back to after a throttle was observed.</summary>
    internal static int ReduceRate(int rate) => Math.Max(rate / 2, MinAdaptiveRate);

    /// <summary>Rate to climb to after a clean interval, capped at the configured rate.</summary>
    internal static int RecoverRate(int rate, int configuredRate) =>
        Math.Min(configuredRate, rate + Math.Max(1, configuredRate / 10));

    /// <summary>
    /// True when the adapted rate is older than the recovery window, so the throttling that
    /// produced it no longer describes the tenant's current state.
    /// </summary>
    internal static bool AdaptedRateHasExpired(long lastThrottleTicks, long nowTicks) =>
        lastThrottleTicks > 0
        && nowTicks - lastThrottleTicks > (long)(AdaptiveRecoveryWindow.TotalSeconds * Stopwatch.Frequency);

    // Classify precedence: drive markers anywhere in the path win, then non-directory service
    // markers anywhere, then the first segment decides directory membership
    private static readonly HashSet<string> DriveMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "drive", "drives", "sites", "shares"
    };

    private static readonly HashSet<string> NonDirectoryMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "messages", "mailfolders", "events", "calendar", "calendars", "calendarview",
        "contactfolders", "chats", "teams", "channels", "teamwork", "onenote", "planner",
        "todo", "photo", "photos", "presence", "insights", "onlinemeetings", "joinedteams"
    };

    private static readonly HashSet<string> DirectoryRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "users", "groups", "serviceprincipals", "applications", "directoryobjects",
        "devices", "directoryroles", "directoryroletemplates", "administrativeunits",
        "oauth2permissiongrants", "organization", "contacts", "directory", "me",
        "grouplifecyclepolicies", "subscribedskus", "domains"
    };

    /// <summary>
    /// Classify a request URI, absolute or relative, into a workload bucket. Never throws.
    /// Unparseable input lands in <see cref="WorkloadBucket.Other"/>.
    /// </summary>
    public static WorkloadBucket Classify(string? requestUri)
    {
        if (string.IsNullOrEmpty(requestUri)) return WorkloadBucket.Other;

        // Reduce to the path. The scheme check matters because on Unix Uri.TryCreate parses a
        // leading-slash relative path as an absolute file:// URI and folds the query into it
        var path = requestUri;
        if (Uri.TryCreate(requestUri, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp))
        {
            path = abs.AbsolutePath;
        }
        else
        {
            var cut = path.IndexOfAny(['?', '#']);
            if (cut >= 0) path = path[..cut];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;

        // Skip the API version segment when present
        if (segments.Length > 0 &&
            (segments[0].Equals("v1.0", StringComparison.OrdinalIgnoreCase)
             || segments[0].Equals("beta", StringComparison.OrdinalIgnoreCase)))
        {
            start = 1;
        }

        if (segments.Length <= start) return WorkloadBucket.Other;

        // $batch first. The envelope URL says nothing about the workloads inside it
        for (var i = start; i < segments.Length; i++)
        {
            if (segments[i].Equals("$batch", StringComparison.OrdinalIgnoreCase))
                return WorkloadBucket.Batch;
        }

        for (var i = start; i < segments.Length; i++)
        {
            if (DriveMarkers.Contains(segments[i])) return WorkloadBucket.Drive;
        }

        for (var i = start; i < segments.Length; i++)
        {
            if (NonDirectoryMarkers.Contains(segments[i])) return WorkloadBucket.Other;
        }

        return DirectoryRoots.Contains(segments[start])
            ? WorkloadBucket.Directory
            : WorkloadBucket.Other;
    }
}
