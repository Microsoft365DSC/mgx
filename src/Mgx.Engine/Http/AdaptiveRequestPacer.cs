using System.Diagnostics;
using System.Globalization;

namespace Mgx.Engine.Http;

/// <summary>
/// Process-wide proactive request pacer. Spaces outgoing requests before the token-bucket lease
/// so the process backs off ahead of the Graph throttle rather than only reacting to a 429.
/// State is partitioned by <see cref="WorkloadBucket"/>.
///
/// Three mechanisms, all at zero delay in a steady clean state:
///  - AIMD cap. A 429 caps the bucket rate, recovery is additive once per clean second, and the
///    cap expires after a quiet AdaptiveRecoveryWindow.
///  - Slow start. A cold bucket opens with a conservative rate cap that doubles each clean
///    second. It caps rather than delays, so the first request is never held back and the cap
///    only bites when demand exceeds it.
///  - Proximity damping. Once Graph reports x-ms-throttle-limit-percentage at or above 0.8, a
///    per-request delay ramps linearly to its maximum at 1.2.
///
/// A 429 Retry-After also pushes the whole bucket next send slot forward, so concurrent callers
/// hold off together.
///
/// The Polly DelayGenerator delays retries of one failed request, this pacer spaces separate
/// requests, so the two compose without double-delaying. Batch outer POSTs skip the gate because
/// GraphBatchClient owns batch throughput, but still feed signal state.
/// </summary>
internal static class AdaptiveRequestPacer
{
    // --- tuning constants ---

    /// <summary>Cap applied on the first 429 in a previously uncapped bucket.</summary>
    internal const int ThrottledEntryRate = 4;

    /// <summary>Opening rate cap for a cold bucket (slow start).</summary>
    internal const int SlowStartInitialRate = 4;

    /// <summary>Clean interval after which caps ramp (recovery / slow-start doubling).</summary>
    internal static readonly TimeSpan RampInterval = TimeSpan.FromSeconds(1);

    /// <summary>How long a reported throttle percentage stays authoritative.</summary>
    internal static readonly TimeSpan PercentageFreshness = TimeSpan.FromSeconds(30);

    /// <summary>Damping starts at 0.8 (per mille: 800) - the documented emission floor.</summary>
    internal const int DampingStartPerMille = 800;

    /// <summary>Damping reaches its maximum at 1.2 (20% of requests already throttled).</summary>
    internal const int DampingFullPerMille = 1200;

    /// <summary>Per-request delay at or above DampingFullPerMille.</summary>
    internal const int DampingMaxDelayMs = 2000;

    private const int Buckets = AdaptivePacing.WorkloadBucketCount;

    // Configuration, set through Configure

    /// <summary>
    /// Test seam. The integration suite disables the gate process-wide via a
    /// [ModuleInitializer] - hundreds of pre-pacing tests would otherwise inherit slow-start
    /// spacing and multiply suite time - and pacer tests re-enable it locally (serially, in
    /// the Pipeline collection). Never set from production code.
    /// </summary>
    internal static volatile bool DisabledForTests;

    private static volatile bool s_enabled = true;
    private static volatile int s_ceilingRate = 50;      // full-recovery target (rps)
    private static volatile int s_maxDelayMs = 120_000;  // MaxRetryAfterSeconds clamp

    // --- per-bucket state ---

    private static readonly object[] s_stateLocks = CreateLocks();
    private static readonly long[] s_nextSlotTicks = new long[Buckets];
    private static readonly long[] s_lastRequestTicks = new long[Buckets];
    private static readonly long[] s_lastThrottleTicks = new long[Buckets];
    private static readonly long[] s_lastRampTicks = new long[Buckets];
    private static readonly int[] s_adaptedRate = new int[Buckets];    // 0 = inactive
    private static readonly int[] s_slowStartRate = new int[Buckets];  // 0 = inactive
    private static readonly int[] s_lastPercentagePerMille = InitPercentages();
    private static readonly long[] s_lastPercentageTicks = new long[Buckets];
    private static readonly long[] s_latencyBaselineMs = new long[Buckets]; // EMA, 0 = no data
    private static readonly long[] s_lastLatencyMs = new long[Buckets];

    private static object[] CreateLocks()
    {
        var locks = new object[Buckets];
        for (var i = 0; i < Buckets; i++) locks[i] = new object();
        return locks;
    }

    private static int[] InitPercentages()
    {
        var p = new int[Buckets];
        Array.Fill(p, -1);
        return p;
    }

    /// <summary>
    /// Apply option-derived configuration. Called from ResiliencePipelineFactory.GetOrCreate,
    /// the chokepoint every client build passes through, so Set-MgxOption changes take effect
    /// on the next cmdlet invocation like every other option.
    /// </summary>
    internal static void Configure(ResilientGraphClientOptions options)
    {
        s_enabled = !options.NoAdaptivePacing;
        // The HTTP token bucket is the hard backstop and the pacer only caps below it. With the
        // bucket disabled there is no configured rate to recover toward, so keep
        // the default ceiling rather than inventing one.
        s_ceilingRate = options.NoRateLimit ? 50 : options.RateLimitPerSecond;
        s_maxDelayMs = options.MaxRetryAfterSeconds * 1000;
    }

    /// <summary>Clears all learned state. Called on credential change
    /// (ResiliencePipelineFactory.Reset) and from test setup.</summary>
    internal static void Reset()
    {
        for (var i = 0; i < Buckets; i++)
        {
            lock (s_stateLocks[i])
            {
                s_nextSlotTicks[i] = 0;
                s_lastRequestTicks[i] = 0;
                s_lastThrottleTicks[i] = 0;
                s_lastRampTicks[i] = 0;
                s_adaptedRate[i] = 0;
                s_slowStartRate[i] = 0;
                s_lastPercentagePerMille[i] = -1;
                s_lastPercentageTicks[i] = 0;
                s_latencyBaselineMs[i] = 0;
                s_lastLatencyMs[i] = 0;
            }
        }
        // Reset clears learned state only: adapted caps, slow start, gauges and baselines.
        // Reverting configuration here would re-enable pacing under a client built with
        // NoAdaptivePacing. Callers changing configuration call Configure
    }

    // --- pure math (the testable core) ---

    /// <summary>Effective rate cap from the two cap sources. 0 = uncapped.</summary>
    internal static int EffectiveRateCap(int adaptedRate, int slowStartRate)
    {
        if (adaptedRate > 0 && slowStartRate > 0) return Math.Min(adaptedRate, slowStartRate);
        return adaptedRate > 0 ? adaptedRate : slowStartRate;
    }

    /// <summary>
    /// Per-request delay from the last reported throttle percentage. Zero below the documented
    /// 0.8 emission floor or when the report has gone stale. Linear ramp to the maximum at 1.2.
    /// </summary>
    internal static long DampingDelayMs(int perMille, long ageTicks)
    {
        if (perMille < DampingStartPerMille) return 0;
        if (ageTicks > (long)(PercentageFreshness.TotalSeconds * Stopwatch.Frequency)) return 0;

        var span = DampingFullPerMille - DampingStartPerMille;
        var over = Math.Min(perMille - DampingStartPerMille, span);
        return DampingMaxDelayMs * over / span;
    }

    /// <summary>Inter-request spacing in Stopwatch ticks: the stricter of the rate cap's
    /// interval and the damping delay. 0 = no spacing.</summary>
    internal static long ComputeIntervalTicks(int rateCap, long dampingDelayMs)
    {
        var capTicks = rateCap > 0 ? Stopwatch.Frequency / rateCap : 0;
        var dampTicks = dampingDelayMs > 0 ? dampingDelayMs * Stopwatch.Frequency / 1000 : 0;
        return Math.Max(capTicks, dampTicks);
    }

    // --- the gate ---

    /// <summary>
    /// Waits until this request may be sent, per the bucket's current pacing state.
    /// Returns the milliseconds actually waited (0 on the fast path). The caller records
    /// telemetry and verbose output so messages land in the right cmdlet's stream.
    /// </summary>
    internal static async ValueTask<long> WaitAsync(WorkloadBucket bucket, CancellationToken cancellationToken)
    {
        // GraphBatchClient passes paceGate false and runs its own item-level AIMD. Guarding here
        // too means a batch cannot claim a slot if a caller forgets the flag, and keeps two AIMD
        // controllers from
        // compounding their backoff on one workload.
        if (bucket == WorkloadBucket.Batch) return 0;

        if (!s_enabled || DisabledForTests) return 0;

        var b = (int)bucket;
        long intervalTicks;

        lock (s_stateLocks[b])
        {
            var now = Stopwatch.GetTimestamp();

            // Expire an adapted cap after a quiet recovery window.
            if (s_adaptedRate[b] > 0 && AdaptivePacing.AdaptedRateHasExpired(s_lastThrottleTicks[b], now))
                s_adaptedRate[b] = 0;

            // A cold bucket enters slow start. An active adapted cap wins, so they do not stack
            var quietTicks = now - s_lastRequestTicks[b];
            var cold = s_lastRequestTicks[b] == 0
                || quietTicks > (long)(AdaptivePacing.AdaptiveRecoveryWindow.TotalSeconds * Stopwatch.Frequency);
            if (cold && s_adaptedRate[b] == 0)
            {
                // Clamp to the ceiling, as the throttle path does, or a low -RateLimitPerSecond
                // gets a slow-start cap above the configured rate, and telemetry
                // reporting "slow-start 4 rps" against a 2 rps ceiling.
                s_slowStartRate[b] = Math.Min(SlowStartInitialRate, s_ceilingRate);
                s_lastRampTicks[b] = now;
            }

            // Caps ramp once per clean interval, additively for the adapted cap and by doubling
            // for slow start. A cap reaching the ceiling deactivates
            var rampTicks = (long)(RampInterval.TotalSeconds * Stopwatch.Frequency);
            if (now - s_lastRampTicks[b] >= rampTicks)
            {
                if (s_adaptedRate[b] > 0 && s_lastThrottleTicks[b] < s_lastRampTicks[b])
                {
                    var next = AdaptivePacing.RecoverRate(s_adaptedRate[b], s_ceilingRate);
                    s_adaptedRate[b] = next >= s_ceilingRate ? 0 : next;
                }
                if (s_slowStartRate[b] > 0)
                {
                    var next = s_slowStartRate[b] * 2;
                    s_slowStartRate[b] = next >= s_ceilingRate ? 0 : next;
                }
                s_lastRampTicks[b] = now;
            }

            s_lastRequestTicks[b] = now;

            var cap = EffectiveRateCap(s_adaptedRate[b], s_slowStartRate[b]);
            var damping = DampingDelayMs(s_lastPercentagePerMille[b], now - s_lastPercentageTicks[b]);
            intervalTicks = ComputeIntervalTicks(cap, damping);
        }

        // Nothing active, though a Retry-After push may still hold the bucket
        long targetTicks;
        long claimNow;
        while (true)
        {
            var next = Interlocked.Read(ref s_nextSlotTicks[b]);
            claimNow = Stopwatch.GetTimestamp();
            targetTicks = Math.Max(claimNow, next);
            if (intervalTicks == 0 && next <= claimNow)
                return 0; // no spacing and no pending hold - don't advance the slot
            var newNext = targetTicks + intervalTicks;
            if (Interlocked.CompareExchange(ref s_nextSlotTicks[b], newNext, next) == next)
                break;
        }

        var delayTicks = targetTicks - claimNow;
        if (delayTicks <= 0) return 0;

        // Make sure the sleep matches the slot advance and clamping synchronizes callers.
        // The wait is not clamped: the slot advances by at most one interval per request, the
        // interval is bounded by MinAdaptiveRate plus DampingMaxDelayMs, and RecordThrottle
        // clamps a server Retry-After. Cancellation is the escape hatch
        var delayMs = delayTicks * 1000 / Stopwatch.Frequency;
        if (delayMs <= 0) return 0;

        await Task.Delay((int)delayMs, cancellationToken);
        return delayMs;
    }

    // --- signal recording ---

    /// <summary>
    /// Record a 429 for the bucket: cap the rate (entry rate on first throttle, halving on
    /// repeat) and, when the server sent Retry-After, push the whole bucket's next send slot
    /// past it so concurrent callers hold off together. Called from the shared pipeline's
    /// OnRetry (before the throttled response is disposed) and on a final 429.
    /// </summary>
    internal static void RecordThrottle(WorkloadBucket bucket, TimeSpan? retryAfter)
    {
        if (!s_enabled || DisabledForTests) return;

        var b = (int)bucket;
        lock (s_stateLocks[b])
        {
            var now = Stopwatch.GetTimestamp();
            s_lastThrottleTicks[b] = now;

            // Batch is never gated, since GraphBatchClient runs its own item-level AIMD and
            // WaitAsync returns before touching state. An adapted cap here would be enforced by
            // nothing and cleared by nothing. The throttle timestamp above is still recorded,
            // and the batch client reads it. Returning also skips the Retry-After slot hold,
            // which nothing on the batch path reads
            if (bucket == WorkloadBucket.Batch)
                return;

            // Both branches clamp to the ceiling, because ReduceRate floors at MinAdaptiveRate
            // and a throttle must never widen the gate
            var reduced = s_adaptedRate[b] > 0
                ? AdaptivePacing.ReduceRate(s_adaptedRate[b])
                : ThrottledEntryRate;
            s_adaptedRate[b] = Math.Min(reduced, s_ceilingRate);
            s_slowStartRate[b] = 0; // the adapted cap governs from here
            s_lastRampTicks[b] = now;
        }

        if (retryAfter is { } ra && ra > TimeSpan.Zero)
        {
            var holdTicks = (long)(Math.Min(ra.TotalMilliseconds, s_maxDelayMs) / 1000.0 * Stopwatch.Frequency);
            var target = Stopwatch.GetTimestamp() + holdTicks;
            while (true)
            {
                var next = Interlocked.Read(ref s_nextSlotTicks[(int)bucket]);
                if (next >= target) break;
                if (Interlocked.CompareExchange(ref s_nextSlotTicks[(int)bucket], target, next) == next)
                    break;
            }
        }
    }

    /// <summary>
    /// Record the final response for a request: throttle-proximity percentage (fed into
    /// damping and surfaced as a telemetry gauge) and a final 429 that exhausted its retries
    /// (OnRetry never fires for the last attempt).
    /// </summary>
    internal static void RecordResponse(WorkloadBucket bucket, HttpResponseMessage response)
    {
        if (!s_enabled || DisabledForTests) return;

        if (response.Headers.TryGetValues("x-ms-throttle-limit-percentage", out var pctValues)
            && double.TryParse(pctValues.FirstOrDefault(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pct)
            && pct > 0)
        {
            var b = (int)bucket;
            Volatile.Write(ref s_lastPercentagePerMille[b], (int)(pct * 1000));
            Volatile.Write(ref s_lastPercentageTicks[b], Stopwatch.GetTimestamp());
        }

        if ((int)response.StatusCode == 429)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
            RecordThrottle(bucket, retryAfter);
        }
    }

    /// <summary>
    /// Record transport latency for the bucket (per attempt, network time only). Maintains a
    /// slow EMA baseline so the SPO soft-clamp - latency stretching 5x+ with no 429s and no
    /// headers - is visible in telemetry. Telemetry-only in 2.1. Not a pacing input.
    /// </summary>
    internal static void RecordLatency(WorkloadBucket bucket, long httpMs)
    {
        if (httpMs <= 0 || !s_enabled || DisabledForTests) return;
        var b = (int)bucket;
        lock (s_stateLocks[b])
        {
            s_lastLatencyMs[b] = httpMs;
            s_latencyBaselineMs[b] = s_latencyBaselineMs[b] == 0
                ? httpMs
                : s_latencyBaselineMs[b] + (httpMs - s_latencyBaselineMs[b]) / 8;
        }
    }

    // --- telemetry surface ---

    /// <summary>Most recently reported throttle percentage across all buckets (raw ratio,
    /// e.g. 0.85), or -1 when never seen.</summary>
    internal static double LastThrottlePercentage
    {
        get
        {
            long bestTicks = 0;
            var best = -1;
            for (var i = 0; i < Buckets; i++)
            {
                var t = Volatile.Read(ref s_lastPercentageTicks[i]);
                if (t > bestTicks)
                {
                    bestTicks = t;
                    best = Volatile.Read(ref s_lastPercentagePerMille[i]);
                }
            }
            return best < 0 ? -1 : best / 1000.0;
        }
    }

    /// <summary>
    /// Human-readable summary of active pacing state per bucket, or null when nothing is
    /// active and no latency data exists. Rendered by Get-MgxTelemetry.
    /// </summary>
    internal static string? DescribeState()
    {
        var parts = new List<string>();
        var now = Stopwatch.GetTimestamp();
        for (var i = 0; i < Buckets; i++)
        {
            var name = ((WorkloadBucket)i).ToString().ToLowerInvariant();
            List<string>? facts = null;
            lock (s_stateLocks[i])
            {
                if (s_adaptedRate[i] > 0)
                {
                    var ago = (now - s_lastThrottleTicks[i]) / Stopwatch.Frequency;
                    (facts ??= []).Add($"capped {s_adaptedRate[i]}/{s_ceilingRate} rps (last 429 {ago}s ago)");
                }
                if (s_slowStartRate[i] > 0)
                    (facts ??= []).Add($"slow-start {s_slowStartRate[i]} rps");
                if (s_lastPercentagePerMille[i] >= DampingStartPerMille
                    && now - s_lastPercentageTicks[i] <= (long)(PercentageFreshness.TotalSeconds * Stopwatch.Frequency))
                    (facts ??= []).Add($"proximity {s_lastPercentagePerMille[i] / 10}%");
                if (s_latencyBaselineMs[i] > 0)
                {
                    var ratio = (double)s_lastLatencyMs[i] / s_latencyBaselineMs[i];
                    (facts ??= []).Add($"latency {s_lastLatencyMs[i]}ms ({ratio:0.0}x of {s_latencyBaselineMs[i]}ms baseline)");
                }
            }
            if (facts != null)
                parts.Add($"{name}: {string.Join(", ", facts)}");
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    // --- test accessors ---

    internal static int GetAdaptedRate(WorkloadBucket bucket) => Volatile.Read(ref s_adaptedRate[(int)bucket]);
    internal static int GetSlowStartRate(WorkloadBucket bucket) => Volatile.Read(ref s_slowStartRate[(int)bucket]);
    internal static long GetNextSlotTicks(WorkloadBucket bucket) => Interlocked.Read(ref s_nextSlotTicks[(int)bucket]);
    internal static bool Enabled => s_enabled;
}
