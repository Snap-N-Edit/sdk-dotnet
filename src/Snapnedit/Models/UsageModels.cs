using System;
using System.Collections.Generic;

namespace Snapnedit;

/// <summary>The dimension a usage series is bucketed along — one at a time.</summary>
public enum UsageGroupBy
{
    /// <summary>One bucket per UTC day, zero-filled across the whole range and ordered oldest first. Wire value <c>"day"</c>.</summary>
    Day = 0,

    /// <summary>One bucket per API key. Wire value <c>"key"</c>.</summary>
    Key = 1,

    /// <summary>One bucket per embed host surface. Wire value <c>"origin"</c>.</summary>
    Origin = 2,

    /// <summary>One bucket per operation id. Wire value <c>"operation"</c>.</summary>
    Operation = 3,

    /// <summary>One bucket per <see cref="UsageSource"/>. Wire value <c>"source"</c>.</summary>
    Source = 4,
}

/// <summary>Wire spellings for <see cref="UsageGroupBy"/>.</summary>
public static class UsageGroupBys
{
    /// <summary>The wire spelling of <paramref name="groupBy"/>.</summary>
    /// <param name="groupBy">The dimension.</param>
    /// <returns>Its wire string.</returns>
    public static string ToWire(UsageGroupBy groupBy) => groupBy switch
    {
        UsageGroupBy.Day => "day",
        UsageGroupBy.Key => "key",
        UsageGroupBy.Origin => "origin",
        UsageGroupBy.Operation => "operation",
        UsageGroupBy.Source => "source",
        _ => throw new ArgumentOutOfRangeException(nameof(groupBy)),
    };

    /// <summary>Parses the <c>groupBy</c> the api echoes back.</summary>
    /// <param name="value">The wire string.</param>
    /// <returns>The matching dimension.</returns>
    /// <exception cref="SnapneditException">The value is not a dimension this package knows.</exception>
    public static UsageGroupBy Parse(string? value) => value switch
    {
        "day" => UsageGroupBy.Day,
        "key" => UsageGroupBy.Key,
        "origin" => UsageGroupBy.Origin,
        "operation" => UsageGroupBy.Operation,
        "source" => UsageGroupBy.Source,
        _ => throw new SnapneditException(
            ErrorCode.Internal,
            "the api returned an unknown usage grouping: " + (value ?? "null"),
            0),
    };
}

/// <summary>How a job was requested, as the api persists it on the job row.</summary>
public enum UsageSource
{
    /// <summary>An <c>sk_</c> API key. Wire value <c>"api"</c>.</summary>
    Api = 0,

    /// <summary>An embedded editor, authenticated with an embed token. Wire value <c>"embed"</c>.</summary>
    Embed = 1,

    /// <summary>A signed-in website visitor. Never billed. Wire value <c>"session"</c>.</summary>
    Session = 2,

    /// <summary>A visitor with no account — never attributed to one, so it never appears here. Wire value <c>"anonymous"</c>.</summary>
    Anonymous = 3,
}

/// <summary>Wire spellings for <see cref="UsageSource"/>.</summary>
public static class UsageSources
{
    /// <summary>The wire spelling of <paramref name="source"/>.</summary>
    /// <param name="source">The source.</param>
    /// <returns>Its wire string.</returns>
    public static string ToWire(UsageSource source) => source switch
    {
        UsageSource.Api => "api",
        UsageSource.Embed => "embed",
        UsageSource.Session => "session",
        UsageSource.Anonymous => "anonymous",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };
}

/// <summary>
/// The query for <see cref="SnapneditClient.GetUsageAsync"/>. Every field is
/// optional: the default window is the last 30 days grouped by day.
/// </summary>
public sealed record UsageQuery
{
    /// <summary>
    /// Inclusive start of the window. Sent as an ISO-8601 UTC instant, so pass
    /// a <see cref="DateTimeOffset"/> in any offset and it is converted for
    /// you. Defaults to 30 days before <see cref="To"/>.
    /// </summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Inclusive end of the window. Defaults to now. A range wider than 366 days is <see cref="ErrorCode.InvalidInput"/>.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Which dimension <c>series</c> is bucketed along. Defaults to <see cref="UsageGroupBy.Day"/>.</summary>
    public UsageGroupBy? GroupBy { get; init; }

    /// <summary>Only jobs authenticated with this API key id. Ignored for an embed token, which is already scoped to its own.</summary>
    public string? KeyId { get; init; }

    /// <summary>Only embed jobs from this host surface — <c>https://app.example.com</c> or <c>native:com.acme.photos</c>.</summary>
    public string? Origin { get; init; }

    /// <summary>Only jobs for this operation id (see <see cref="Operations.All"/>).</summary>
    public string? Operation { get; init; }

    /// <summary>Only jobs that arrived this way.</summary>
    public UsageSource? Source { get; init; }
}

/// <summary>The window the api actually applied, echoed back as ISO-8601 instants.</summary>
/// <remarks>
/// These are INSTANTS, not the <c>YYYY-MM-DD</c> the query accepts: a bare
/// date <c>to</c> comes back snapped to the last millisecond of that UTC day.
/// </remarks>
public sealed record UsageRange
{
    /// <summary>Creates a range.</summary>
    /// <param name="from">ISO-8601 start instant.</param>
    /// <param name="to">ISO-8601 end instant.</param>
    public UsageRange(string from, string to)
    {
        From = from;
        To = to;
    }

    /// <summary>ISO-8601 instant the window starts at, inclusive.</summary>
    public string From { get; }

    /// <summary>ISO-8601 instant the window ends at, inclusive.</summary>
    public string To { get; }
}

/// <summary>
/// The seven counters every usage bucket carries.
/// </summary>
/// <remarks>
/// They count REQUESTS rather than model runs: a cache hit is one more
/// <see cref="Jobs"/> and one more <see cref="CacheHits"/>, billed nothing.
/// </remarks>
public record UsageFacts
{
    /// <summary>Creates a set of counters.</summary>
    /// <param name="jobs">Jobs created, cache hits included.</param>
    /// <param name="credits">Credits actually debited.</param>
    /// <param name="cacheHits">Jobs served from the result cache.</param>
    /// <param name="free">Jobs whose operation costs 0 credits.</param>
    /// <param name="failed">Jobs that ended failed (and were refunded).</param>
    /// <param name="delivered">Jobs whose result reached a storage destination.</param>
    /// <param name="deliveryFailed">Jobs whose delivery failed.</param>
    public UsageFacts(int jobs, int credits, int cacheHits, int free, int failed, int delivered, int deliveryFailed)
    {
        Jobs = jobs;
        Credits = credits;
        CacheHits = cacheHits;
        Free = free;
        Failed = failed;
        Delivered = delivered;
        DeliveryFailed = deliveryFailed;
    }

    /// <summary>Jobs created — one per request, cache hits and delivery-only clones included.</summary>
    public int Jobs { get; }

    /// <summary>Credits actually debited. A free operation, a cache hit and an unmetered website job all contribute 0.</summary>
    public int Credits { get; }

    /// <summary>Jobs served from the result cache instead of by running a model.</summary>
    public int CacheHits { get; }

    /// <summary>Jobs whose operation costs 0 credits.</summary>
    public int Free { get; }

    /// <summary>Jobs that ended <c>failed</c>. Their credits were refunded.</summary>
    public int Failed { get; }

    /// <summary>Jobs whose result was successfully PUT to a storage destination.</summary>
    public int Delivered { get; }

    /// <summary>Jobs whose delivery failed. The job itself may still have succeeded — a failed delivery never fails a job.</summary>
    public int DeliveryFailed { get; }
}

/// <summary>The whole range, undivided, plus the two embed-session counts no series bucket can always carry.</summary>
public sealed record UsageTotals : UsageFacts
{
    /// <summary>Creates the totals.</summary>
    /// <param name="jobs">Jobs created, cache hits included.</param>
    /// <param name="credits">Credits actually debited.</param>
    /// <param name="cacheHits">Jobs served from the result cache.</param>
    /// <param name="free">Jobs whose operation costs 0 credits.</param>
    /// <param name="failed">Jobs that ended failed.</param>
    /// <param name="delivered">Jobs whose result reached a storage destination.</param>
    /// <param name="deliveryFailed">Jobs whose delivery failed.</param>
    /// <param name="sessions">Embed sessions started in the range.</param>
    /// <param name="activeSessions">Embed sessions last seen within the range.</param>
    public UsageTotals(
        int jobs,
        int credits,
        int cacheHits,
        int free,
        int failed,
        int delivered,
        int deliveryFailed,
        int sessions,
        int activeSessions)
        : base(jobs, credits, cacheHits, free, failed, delivered, deliveryFailed)
    {
        Sessions = sessions;
        ActiveSessions = activeSessions;
    }

    /// <summary>Embedded-editor sessions STARTED in the range. 0 for an account that does not use the embed.</summary>
    public int Sessions { get; }

    /// <summary>Embedded-editor sessions last seen within the range, whenever they started.</summary>
    public int ActiveSessions { get; }
}

/// <summary>One bucket of the series: a day, a key, an origin, an operation or a source, per the requested grouping.</summary>
public sealed record UsageSeriesEntry : UsageFacts
{
    /// <summary>Creates a series entry.</summary>
    /// <param name="key">The dimension value.</param>
    /// <param name="label">A human label for the key.</param>
    /// <param name="jobs">Jobs created, cache hits included.</param>
    /// <param name="credits">Credits actually debited.</param>
    /// <param name="cacheHits">Jobs served from the result cache.</param>
    /// <param name="free">Jobs whose operation costs 0 credits.</param>
    /// <param name="failed">Jobs that ended failed.</param>
    /// <param name="delivered">Jobs whose result reached a storage destination.</param>
    /// <param name="deliveryFailed">Jobs whose delivery failed.</param>
    /// <param name="sessions">Embed sessions started in this bucket.</param>
    public UsageSeriesEntry(
        string key,
        string label,
        int jobs,
        int credits,
        int cacheHits,
        int free,
        int failed,
        int delivered,
        int deliveryFailed,
        int sessions)
        : base(jobs, credits, cacheHits, free, failed, delivered, deliveryFailed)
    {
        Key = key;
        Label = label;
        Sessions = sessions;
    }

    /// <summary>
    /// The dimension value — a <c>YYYY-MM-DD</c>, an API key id, an origin, an
    /// operation id or a source. <see cref="UsageReport.Unattributed"/> when
    /// the rows had no value for the grouped dimension.
    /// </summary>
    public string Key { get; }

    /// <summary>A human label for <see cref="Key"/>: the key's NAME when grouping by key, the key itself otherwise.</summary>
    public string Label { get; }

    /// <summary>Embed sessions started in this bucket. Always 0 when grouping by operation or source, which sessions have no dimension for.</summary>
    public int Sessions { get; }
}

/// <summary>One API key on the account, with what it has spent TODAY against its daily cap.</summary>
public sealed record UsageKeyGauge
{
    /// <summary>Creates a key gauge row.</summary>
    /// <param name="id">The API key id.</param>
    /// <param name="name">The key's label.</param>
    /// <param name="kind"><c>"secret"</c> or <c>"publishable"</c>.</param>
    /// <param name="dailyCreditLimit">The per-UTC-day credit ceiling, or <see langword="null"/> for uncapped.</param>
    /// <param name="usedToday">Credits spent so far today (UTC).</param>
    public UsageKeyGauge(string id, string name, string kind, int? dailyCreditLimit, int usedToday)
    {
        Id = id;
        Name = name;
        Kind = kind;
        DailyCreditLimit = dailyCreditLimit;
        UsedToday = usedToday;
    }

    /// <summary>The API key id — the same id a <see cref="UsageGroupBy.Key"/> series buckets under.</summary>
    public string Id { get; }

    /// <summary>The label the key was created with, and the <see cref="UsageSeriesEntry.Label"/> of its bucket.</summary>
    public string Name { get; }

    /// <summary><c>"secret"</c> or <c>"publishable"</c>.</summary>
    public string Kind { get; }

    /// <summary>The per-UTC-day credit ceiling; <see langword="null"/> is unlimited. Only publishable keys are capped.</summary>
    public int? DailyCreditLimit { get; }

    /// <summary>Credits this key has spent so far TODAY (UTC), whatever range was requested.</summary>
    public int UsedToday { get; }

    /// <summary>Whether this key has a daily ceiling at all.</summary>
    public bool IsCapped => DailyCreditLimit.HasValue;

    /// <summary>What is left of <see cref="DailyCreditLimit"/> today, or <see langword="null"/> for an uncapped key. Never negative.</summary>
    public int? RemainingToday =>
        DailyCreditLimit.HasValue ? Math.Max(DailyCreditLimit.Value - UsedToday, 0) : (int?)null;
}

/// <summary>The <c>GET /usage</c> body: the resolved window, the undivided totals, the grouped series and the key gauge.</summary>
public sealed record UsageReport
{
    /// <summary>Creates a usage report.</summary>
    /// <param name="range">The window the api applied.</param>
    /// <param name="groupBy">The dimension the series is bucketed along.</param>
    /// <param name="totals">The whole range, undivided.</param>
    /// <param name="series">One entry per value of the grouped dimension.</param>
    /// <param name="keys">The account's live API keys; empty for an embed token.</param>
    public UsageReport(
        UsageRange range,
        UsageGroupBy groupBy,
        UsageTotals totals,
        IReadOnlyList<UsageSeriesEntry> series,
        IReadOnlyList<UsageKeyGauge> keys)
    {
        Range = range;
        GroupBy = groupBy;
        Totals = totals;
        Series = series;
        Keys = keys;
    }

    /// <summary>The series key for rows with no value for the grouped dimension — an <c>sk_</c> job has no origin, a website job has no key.</summary>
    public static string Unattributed => "none";

    /// <summary>The window the api actually applied.</summary>
    public UsageRange Range { get; }

    /// <summary>The dimension <see cref="Series"/> is bucketed along, as echoed by the api.</summary>
    public UsageGroupBy GroupBy { get; }

    /// <summary>Every job in the range, undivided.</summary>
    public UsageTotals Totals { get; }

    /// <summary>One bucket per value of the grouped dimension: oldest first for <see cref="UsageGroupBy.Day"/>, busiest first otherwise.</summary>
    public IReadOnlyList<UsageSeriesEntry> Series { get; }

    /// <summary>
    /// Every live API key on the account, with today's spend. EMPTY for an
    /// embed-token caller: a page-scoped credential may not enumerate the
    /// account's keys, so the api omits the gauge entirely.
    /// </summary>
    public IReadOnlyList<UsageKeyGauge> Keys { get; }

    /// <summary>One bucket by its dimension value.</summary>
    /// <param name="key">The bucket key, e.g. <c>"upscale"</c> or <c>"2026-09-13"</c>.</param>
    /// <returns>The bucket, or <see langword="null"/> when the series has none.</returns>
    public UsageSeriesEntry? Bucket(string key)
    {
        foreach (var entry in Series)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
