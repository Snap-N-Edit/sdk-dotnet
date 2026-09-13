using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Snapnedit.Internal;

/// <summary>Narrows api responses into this package's model types, field by field.</summary>
internal static class ApiParse
{
    internal static SignedUrl SignedUrl(JsonElement value, string source)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Json.Malformed(source, "expected a signed-url object");
        }

        return new SignedUrl(Json.RequireString(value, "url", source), Json.RequireString(value, "expiresAt", source));
    }

    /// <summary>
    /// Reads a job's status off <paramref name="obj"/>. The api nests it under
    /// <c>status</c> on <c>POST /jobs</c> and FLATTENS it to the top level on
    /// <c>GET /jobs/{id}</c>; both shapes reach here as the object holding
    /// <c>state</c>.
    /// </summary>
    internal static JobStatus JobStatus(JsonElement obj, string source)
    {
        var state = Json.OptionalString(obj, "state");
        switch (state)
        {
            case "queued":
                return new JobStatus(JobState.Queued);
            case "processing":
                return new JobStatus(JobState.Processing, startedAt: Json.RequireString(obj, "startedAt", source));
            case "succeeded":
            {
                var outputAssetId = Json.RequireString(obj, "outputAssetId", source);
                var download = Json.Property(obj, "download");
                // `download: null` is a real, documented outcome, not a
                // malformed body: a job delivered to a saved destination with
                // deleteAfterDelivery has no copy left on our side to sign.
                var signed = download is null || download.Value.ValueKind == JsonValueKind.Null
                    ? null
                    : SignedUrl(download.Value, source);
                return new JobStatus(JobState.Succeeded, outputAssetId: outputAssetId, download: signed);
            }

            case "failed":
                return new JobStatus(
                    JobState.Failed,
                    errorCode: ErrorCodes.Parse(Json.OptionalString(obj, "errorCode")),
                    message: Json.RequireString(obj, "message", source));
            case "canceled":
                return new JobStatus(JobState.Canceled);
            default:
                throw Json.Malformed(source, "unknown job state \"" + (state ?? "null") + "\"");
        }
    }

    /// <summary>
    /// Narrows the storage + usage-attribution envelope. Deliberately LENIENT
    /// where the rest of this file is strict: an api that predates either
    /// feature sends none of these keys, and a plain upload-and-download caller
    /// must not start throwing against such a deployment. An absent
    /// <c>creditCost</c> reads as 0 and an absent flag as false — which is what
    /// a deployment that does not report them means.
    /// </summary>
    internal static JobEnvelope JobEnvelope(JsonElement obj)
    {
        var input = Json.ObjectProperty(obj, "input");
        var kind = input is not null && Json.OptionalString(input.Value, "kind") == "url"
            ? JobInputKind.Url
            : JobInputKind.Asset;
        var delivery = Json.ObjectProperty(obj, "delivery");
        return new JobEnvelope(
            kind,
            JobDestinationSummary(Json.ObjectProperty(obj, "destination")),
            delivery is null ? null : JobDelivery(delivery.Value),
            Json.OptionalInt(obj, "creditCost") ?? 0,
            Json.OptionalBool(obj, "cached") ?? false,
            Json.OptionalBool(obj, "deliveryOnly") ?? false);
    }

    /// <summary>A destination summary, or <see langword="null"/> for anything unrecognized — never a throw.</summary>
    internal static JobDestinationSummary? JobDestinationSummary(JsonElement? obj)
    {
        if (obj is null)
        {
            return null;
        }

        var type = Json.OptionalString(obj.Value, "type");
        if (type == "presigned-put")
        {
            return new JobDestinationSummary("presigned-put");
        }

        if (type == "saved")
        {
            var id = Json.OptionalString(obj.Value, "id");
            if (id is not null)
            {
                return new JobDestinationSummary("saved", id, Json.OptionalString(obj.Value, "name"));
            }
        }

        return null;
    }

    /// <summary>A delivery record. An unrecognized status degrades to pending rather than throwing.</summary>
    internal static JobDelivery JobDelivery(JsonElement obj)
    {
        var status = Json.OptionalString(obj, "status") switch
        {
            "delivered" => JobDeliveryStatus.Delivered,
            "failed" => JobDeliveryStatus.Failed,
            _ => JobDeliveryStatus.Pending,
        };
        return new JobDelivery(
            status,
            Json.OptionalInt(obj, "attempts") ?? 0,
            Json.OptionalString(obj, "deliveredAt"),
            Json.OptionalInt(obj, "statusCode"),
            Json.OptionalString(obj, "error"),
            Json.OptionalString(obj, "bucket"),
            Json.OptionalString(obj, "key"),
            Json.OptionalBool(obj, "localCopyDeleted"));
    }

    internal static JobView JobView(string jobId, JsonElement obj, string source)
    {
        var envelope = JobEnvelope(obj);
        return new JobView(
            jobId,
            JobStatus(obj, source),
            envelope.InputKind,
            envelope.Destination,
            envelope.Delivery,
            envelope.CreditCost,
            envelope.Cached,
            envelope.DeliveryOnly);
    }

    internal static CreateJobResult CreateJobResult(JsonElement obj, int httpStatus, string source)
    {
        var status = Json.ObjectProperty(obj, "status");
        if (status is null)
        {
            throw Json.Malformed(source, "missing object field \"status\"");
        }

        var envelope = JobEnvelope(obj);
        return new CreateJobResult(
            Json.RequireString(obj, "jobId", source),
            JobStatus(status.Value, source),
            httpStatus,
            envelope.InputKind,
            envelope.Destination,
            envelope.Delivery,
            envelope.CreditCost,
            envelope.Cached,
            envelope.DeliveryOnly);
    }

    internal static OperationMetadata OperationMetadata(JsonElement obj, string source)
    {
        var accept = new List<string>();
        var acceptElement = Json.Property(obj, "accept");
        if (acceptElement is not null && acceptElement.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in acceptElement.Value.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    accept.Add(entry.GetString()!);
                }
            }
        }

        var schema = Json.Property(obj, "paramsJsonSchema");
        return new OperationMetadata(
            Json.RequireString(obj, "id", source),
            Json.RequireString(obj, "label", source),
            accept,
            Json.OptionalInt(obj, "maxInputDimension") ?? 0,
            Json.RequireBool(obj, "requiresMask", source),
            Json.RequireString(obj, "description", source),
            Json.RequireString(obj, "seoTitle", source),
            // Cloned: the JsonDocument this element belongs to is disposed as
            // soon as the response is parsed.
            schema?.Clone() ?? default);
    }

    internal static StorageDestination Destination(JsonElement obj, string source)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw Json.Malformed(source, "expected a storage destination object");
        }

        var lastTest = Json.ObjectProperty(obj, "lastTest");
        return new StorageDestination(
            Json.RequireString(obj, "id", source),
            Json.RequireString(obj, "name", source),
            StorageProviders.Parse(Json.OptionalString(obj, "provider")),
            Json.RequireString(obj, "bucket", source),
            Json.OptionalString(obj, "region"),
            Json.OptionalString(obj, "endpoint"),
            Json.RequireBool(obj, "forcePathStyle", source),
            Json.RequireString(obj, "keyPrefix", source),
            Json.RequireString(obj, "accessKeyIdLast4", source),
            Json.RequireBool(obj, "isDefault", source),
            Json.RequireBool(obj, "deleteAfterDelivery", source),
            lastTest is null
                ? null
                : new StorageDestinationTest(
                    Json.OptionalString(lastTest.Value, "status") == "ok",
                    Json.RequireString(lastTest.Value, "at", source),
                    Json.OptionalString(lastTest.Value, "error")),
            Json.RequireString(obj, "createdAt", source),
            Json.RequireString(obj, "updatedAt", source));
    }

    internal static DestinationTestResult DestinationTestResult(JsonElement obj, string source)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw Json.Malformed(source, "expected a destination test result");
        }

        var ok = Json.OptionalBool(obj, "ok") ?? false;
        var latency = Json.OptionalDouble(obj, "latencyMs") ?? 0d;
        return ok
            ? new DestinationTestResult(true, latency)
            : new DestinationTestResult(false, latency, Json.OptionalString(obj, "error") ?? "destination test failed");
    }

    internal static DestinationPresignResponse PresignResponse(JsonElement obj, string source)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerObject = Json.ObjectProperty(obj, "headers");
        if (headerObject is not null)
        {
            foreach (var property in headerObject.Value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    headers[property.Name] = property.Value.GetString()!;
                }
            }
        }

        return new DestinationPresignResponse(
            Json.RequireString(obj, "url", source),
            "PUT",
            headers,
            Json.RequireString(obj, "key", source),
            Json.RequireString(obj, "bucket", source),
            Json.RequireString(obj, "expiresAt", source));
    }

    /// <summary>
    /// Narrows the <c>GET /usage</c> body. Strict about the structure (a
    /// missing <c>range</c>, <c>totals</c> or <c>series</c> is a malformed
    /// response) and lenient about the counters: every one of them is a count,
    /// so an absent field reads as 0 rather than throwing.
    /// </summary>
    internal static UsageReport UsageReport(JsonElement obj, string source)
    {
        var range = Json.ObjectProperty(obj, "range");
        if (range is null)
        {
            throw Json.Malformed(source, "missing object field \"range\"");
        }

        var totals = Json.ObjectProperty(obj, "totals");
        if (totals is null)
        {
            throw Json.Malformed(source, "missing object field \"totals\"");
        }

        var series = Json.Property(obj, "series");
        if (series is null || series.Value.ValueKind != JsonValueKind.Array)
        {
            throw Json.Malformed(source, "missing array field \"series\"");
        }

        var entries = new List<UsageSeriesEntry>();
        foreach (var entry in series.Value.EnumerateArray())
        {
            entries.Add(UsageSeriesEntry(entry, source));
        }

        // `keys` is absent for an embed token, which may not enumerate the
        // account's keys. An empty list keeps every caller off a null check.
        var keys = new List<UsageKeyGauge>();
        var gauge = Json.Property(obj, "keys");
        if (gauge is not null && gauge.Value.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in gauge.Value.EnumerateArray())
            {
                keys.Add(UsageKeyGauge(entry, source));
            }
        }

        return new UsageReport(
            new UsageRange(
                Json.RequireString(range.Value, "from", source),
                Json.RequireString(range.Value, "to", source)),
            UsageGroupBys.Parse(Json.OptionalString(obj, "groupBy")),
            new UsageTotals(
                Count(totals.Value, "jobs"),
                Count(totals.Value, "credits"),
                Count(totals.Value, "cacheHits"),
                Count(totals.Value, "free"),
                Count(totals.Value, "failed"),
                Count(totals.Value, "delivered"),
                Count(totals.Value, "deliveryFailed"),
                Count(totals.Value, "sessions"),
                Count(totals.Value, "activeSessions")),
            entries,
            keys);
    }

    private static UsageSeriesEntry UsageSeriesEntry(JsonElement obj, string source) =>
        new UsageSeriesEntry(
            Json.RequireString(obj, "key", source),
            Json.OptionalString(obj, "label") ?? Json.RequireString(obj, "key", source),
            Count(obj, "jobs"),
            Count(obj, "credits"),
            Count(obj, "cacheHits"),
            Count(obj, "free"),
            Count(obj, "failed"),
            Count(obj, "delivered"),
            Count(obj, "deliveryFailed"),
            Count(obj, "sessions"));

    private static UsageKeyGauge UsageKeyGauge(JsonElement obj, string source) =>
        new UsageKeyGauge(
            Json.RequireString(obj, "id", source),
            Json.RequireString(obj, "name", source),
            Json.RequireString(obj, "kind", source),
            // Explicitly nullable on the wire: null means "no daily cap".
            Json.OptionalInt(obj, "dailyCreditLimit"),
            Count(obj, "usedToday"));

    /// <summary>A usage counter: absent and zero mean the same thing for a count.</summary>
    private static int Count(JsonElement obj, string name) => Json.OptionalInt(obj, name) ?? 0;

    internal static EmbedToken EmbedToken(JsonElement obj, string source) =>
        new EmbedToken(Json.RequireString(obj, "token", source), Json.RequireString(obj, "expiresAt", source));
}
