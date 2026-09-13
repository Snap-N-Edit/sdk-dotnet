using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

namespace Snapnedit.Tests.Conformance;

/// <summary>
/// Every scenario in <c>test/conformance/scenarios.json</c>, one test each,
/// named with the scenario id — so when a scenario changes, <c>grep</c> finds
/// the test.
/// </summary>
/// <remarks>
/// The suite drives the real conformance server (a real OS process on an
/// ephemeral port, running the production api and worker). Steps that are about
/// the WIRE — an exact status code, a request with no credential, a helper
/// endpoint — go through <see cref="Rest"/>; everything else goes through the
/// SDK, so passing proves the CLIENT is right and not just the server.
/// </remarks>
[Trait("Category", "Conformance")]
public sealed class ConformanceTests : IClassFixture<ConformanceServerFixture>
{
    private static readonly string[] ExpectedOperationIds =
    {
        "remove-background", "upscale", "unblur", "colorize", "style-transfer", "retouch", "beautify",
        "magic-eraser", "generative-fill", "remove-watermark", "ai-denoise", "replace-sky", "relight",
        "replace-background", "strip-metadata", "auto-remove-watermark", "resize-image",
    };

    private readonly ConformanceServerFixture _fx;

    /// <summary>Binds the suite to the running conformance server.</summary>
    /// <param name="fixture">The shared server fixture.</param>
    public ConformanceTests(ConformanceServerFixture fixture) => _fx = fixture;

    /* ---------------- auth ---------------- */

    [Fact(DisplayName = "auth.missing-credential")]
    public async Task AuthMissingCredential()
    {
        var response = await _fx.Anonymous.SendAsync("GET", "/destinations");

        Assert.Equal(401, response.Status);
        Assert.Equal(new[] { "error" }, TopLevelKeys(response.Json));
        Assert.Equal("unauthorized", response.ErrorCode);
        Assert.NotEmpty(response.ErrorMessage!);
    }

    [Fact(DisplayName = "auth.bad-key")]
    public async Task AuthBadKey()
    {
        var response = await _fx.Anonymous.SendAsync(
            "GET",
            "/destinations",
            authorization: "Bearer sk_live_this_key_does_not_exist");

        Assert.Equal(401, response.Status);
        Assert.Equal("unauthorized", response.ErrorCode);

        // The SDK surfaces the same thing as a typed error.
        using var bogus = _fx.NewClient("sk_live_this_key_does_not_exist");
        var error = await Assert.ThrowsAsync<SnapneditException>(() => bogus.Destinations.ListAsync());
        Assert.Equal(ErrorCode.Unauthorized, error.Code);
        Assert.Equal(401, error.Status);
    }

    [Fact(DisplayName = "auth.publishable-key-is-not-a-bearer")]
    public async Task AuthPublishableKeyIsNotABearer()
    {
        var response = await _fx.Anonymous.SendAsync(
            "GET",
            "/destinations",
            authorization: "Bearer " + _fx.PublishableKey);

        // A pk_ key is only ever exchanged at POST /embed/sessions.
        Assert.Equal(401, response.Status);
        Assert.Equal("unauthorized", response.ErrorCode);
    }

    /* ---------------- operations ---------------- */

    [Fact(DisplayName = "operations.list")]
    public async Task OperationsList()
    {
        var response = await _fx.Anonymous.SendAsync("GET", "/operations");
        Assert.Equal(200, response.Status);

        var entries = response.Json.EnumerateArray().ToList();
        Assert.Equal(17, entries.Count);
        Assert.Equal(ExpectedOperationIds, entries.Select(e => e.GetProperty("id").GetString()).ToArray());

        foreach (var entry in entries)
        {
            Assert.NotEmpty(entry.GetProperty("label").GetString()!);
            Assert.NotEmpty(entry.GetProperty("accept").EnumerateArray().ToList());
            Assert.True(entry.GetProperty("maxInputDimension").GetInt32() > 0);
            Assert.Equal(JsonValueKind.Object, entry.GetProperty("paramsJsonSchema").ValueKind);
            Assert.NotEmpty(entry.GetProperty("description").GetString()!);
            Assert.NotEmpty(entry.GetProperty("seoTitle").GetString()!);
        }

        Assert.Equal(
            new[] { "generative-fill", "magic-eraser", "remove-watermark" },
            entries.Where(e => e.GetProperty("requiresMask").GetBoolean())
                .Select(e => e.GetProperty("id").GetString()!)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray());

        // The same catalog, through the SDK.
        var operations = await _fx.Api.ListOperationsAsync();
        Assert.Equal(ExpectedOperationIds, operations.Select(o => o.Id).ToArray());
        Assert.Equal(Operations.All, operations.Select(o => o.Id).ToList());
        Assert.Equal(
            Operations.RequiringMask.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            operations.Where(o => o.RequiresMask).Select(o => o.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());

        var upscale = operations.Single(o => o.Id == Operations.Upscale);
        var factor = upscale.ParamsJsonSchema.GetProperty("properties").GetProperty("factor");
        Assert.Equal(new[] { "2", "4" }, factor.GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToArray());
        Assert.Equal("2", factor.GetProperty("default").GetString());

        // Credit costs are published on the OpenAPI document, per operation.
        var spec = await _fx.Anonymous.SendAsync("GET", "/openapi.json");
        var costs = spec.Json.GetProperty("paths").GetProperty("/operations").GetProperty("get").GetProperty("x-credit-cost");
        Assert.Equal(0, costs.GetProperty("resize-image").GetInt32());
        Assert.Equal(1, costs.GetProperty("remove-background").GetInt32());
        Assert.Equal(2, costs.GetProperty("upscale").GetInt32());
        Assert.Equal(
            0,
            spec.Json.GetProperty("components").GetProperty("schemas")
                .GetProperty("OperationParams.resize-image").GetProperty("x-credit-cost").GetInt32());

        // …and this package's own constants agree with them.
        foreach (var property in costs.EnumerateObject())
        {
            Assert.Equal(property.Value.GetInt32(), Operations.CreditCosts[property.Name]);
        }
    }

    /* ---------------- the core job flow ---------------- */

    [Fact(DisplayName = "jobs.happy-path")]
    public async Task JobsHappyPath()
    {
        var bytes = await FixtureAsync(Nonce("happy"));

        var created = await _fx.Authenticated.SendAsync(
            "POST",
            "/uploads",
            "{\"mime\":\"image/png\",\"bytes\":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "}");
        Assert.Equal(200, created.Status);
        var assetId = created.Json.GetProperty("assetId").GetString()!;
        var uploadUrl = created.Json.GetProperty("upload").GetProperty("url").GetString()!;
        Assert.NotEmpty(created.Json.GetProperty("upload").GetProperty("expiresAt").GetString()!);

        // The presigned url is a PATH — resolve it against the base.
        var put = await _fx.Authenticated.SendBytesAsync(
            "PUT",
            uploadUrl,
            bytes,
            new Dictionary<string, string> { ["content-type"] = "image/png" });
        Assert.Equal(204, put.Status);

        var confirmed = await _fx.Authenticated.SendAsync("POST", "/uploads/" + assetId + "/confirm");
        Assert.Equal(200, confirmed.Status);
        Assert.Equal(assetId, confirmed.Json.GetProperty("assetId").GetString());
        Assert.Matches("^[0-9a-f]{64}$", confirmed.Json.GetProperty("contentHash").GetString()!);
        Assert.Equal(bytes.Length, confirmed.Json.GetProperty("bytes").GetInt32());

        var job = await _fx.Authenticated.SendAsync(
            "POST",
            "/jobs",
            "{\"operation\":\"remove-background\",\"inputAssetId\":\"" + assetId + "\"}");
        Assert.Equal(202, job.Status);
        // POST /jobs NESTS the status; GET /jobs/{id} FLATTENS it.
        Assert.Equal("queued", job.Json.GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("asset", job.Json.GetProperty("input").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, job.Json.GetProperty("destination").ValueKind);
        Assert.Equal(JsonValueKind.Null, job.Json.GetProperty("delivery").ValueKind);

        var jobId = job.Json.GetProperty("jobId").GetString()!;
        var done = await PollRawAsync(jobId);
        Assert.Equal("succeeded", done.GetProperty("state").GetString());
        Assert.NotEmpty(done.GetProperty("outputAssetId").GetString()!);
        var download = done.GetProperty("download").GetProperty("url").GetString()!;

        var downloaded = await _fx.Authenticated.GetBytesAsync(download);
        Assert.Equal(200, downloaded.Status);
        Assert.NotEmpty(downloaded.Bytes);
        Assert.Contains("image/png", downloaded.ContentType!, StringComparison.Ordinal);

        // The same flow, in one SDK call.
        var run = await _fx.Api.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromBytes(await FixtureAsync(Nonce("happy-sdk")), "image/png"),
            FastPoll());
        Assert.True(run.Downloaded);
        Assert.NotEmpty(run.Output!);
        Assert.Equal("image/png", run.Mime);
        Assert.Equal(JobInputKind.Asset, run.InputKind);
        Assert.Null(run.Destination);
        Assert.Null(run.Delivery);
    }

    [Fact(DisplayName = "jobs.params-validation")]
    public async Task JobsParamsValidation()
    {
        var asset = await UniqueUploadAsync("params");

        var error = await Assert.ThrowsAsync<SnapneditException>(() => _fx.Api.CreateJobAsync(
            Operations.Upscale,
            JobInput.FromAssetId(asset.AssetId),
            new CreateJobOptions { Params = new Dictionary<string, object?> { ["factor"] = "3" } }));
        Assert.Equal(ErrorCode.InvalidInput, error.Code);
        Assert.Equal(400, error.Status);

        // No job was created and nothing was debited.
        var accepted = await _fx.Api.CreateJobAsync(
            Operations.Upscale,
            JobInput.FromAssetId(asset.AssetId),
            new CreateJobOptions { TypedParams = new UpscaleParams { Factor = UpscaleFactor.X4 } });
        Assert.Equal(202, accepted.HttpStatus);
    }

    [Fact(DisplayName = "jobs.unknown-param-rejected")]
    public async Task JobsUnknownParamRejected()
    {
        var asset = await UniqueUploadAsync("strict-params");

        var error = await Assert.ThrowsAsync<SnapneditException>(() => _fx.Api.CreateJobAsync(
            Operations.Upscale,
            JobInput.FromAssetId(asset.AssetId),
            new CreateJobOptions
            {
                Params = new Dictionary<string, object?> { ["factor"] = "2", ["nope"] = "x" },
            }));

        // Every operation's params schema is strict: an unknown key is refused,
        // never ignored.
        Assert.Equal(ErrorCode.InvalidInput, error.Code);
        Assert.Equal(400, error.Status);
    }

    /* ---------------- credits ---------------- */

    [Fact(DisplayName = "jobs.free-operation")]
    public async Task JobsFreeOperation()
    {
        var before = await BalanceAsync();
        var asset = await UniqueUploadAsync("free-op");

        var created = await _fx.Api.CreateJobAsync(
            Operations.ResizeImage,
            JobInput.FromAssetId(asset.AssetId),
            new CreateJobOptions { TypedParams = new ResizeImageParams { Width = 4 } });
        Assert.Equal(202, created.HttpStatus);
        // The job says so itself, not just the ledger.
        Assert.Equal(0, created.CreditCost);
        Assert.False(created.Cached);
        var done = await _fx.Api.WaitForJobAsync(created.JobId, FastWait());

        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(0, done.CreditCost);
        // resize-image costs 0 and writes no ledger entry.
        Assert.Equal(before, await BalanceAsync());
    }

    [Fact(DisplayName = "jobs.credit-debit")]
    public async Task JobsCreditDebit()
    {
        var before = await BalanceAsync();
        var asset = await UniqueUploadAsync("debit");

        var created = await _fx.Api.CreateJobAsync(Operations.RemoveBackground, JobInput.FromAssetId(asset.AssetId));
        Assert.Equal(202, created.HttpStatus);
        Assert.Equal(1, created.CreditCost);

        // The debit lands at creation time, before the worker ever sees the job.
        Assert.Equal(before - 1, await BalanceAsync());
        var done = await _fx.Api.WaitForJobAsync(created.JobId, FastWait());
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(before - 1, await BalanceAsync());
    }

    [Fact(DisplayName = "jobs.cache-hit")]
    public async Task JobsCacheHit()
    {
        var asset = await UniqueUploadAsync("cache");
        var first = await _fx.Api.CreateJobAsync(Operations.RemoveBackground, JobInput.FromAssetId(asset.AssetId));
        Assert.Equal(202, first.HttpStatus);
        var done = await _fx.Api.WaitForJobAsync(first.JobId, FastWait());
        Assert.Equal(JobState.Succeeded, done.State);
        var outputAssetId = done.Status.OutputAssetId;

        var before = await BalanceAsync();
        var second = await _fx.Api.CreateJobAsync(Operations.RemoveBackground, JobInput.FromAssetId(asset.AssetId));

        Assert.Equal(200, second.HttpStatus);
        Assert.True(second.WasCacheHit);
        Assert.True(second.Cached);
        Assert.Equal(0, second.CreditCost);
        Assert.False(first.Cached);
        Assert.Equal(JobState.Succeeded, second.Status.State);
        // A hit still records a job — one row per REQUEST, so usage can tell
        // what was asked for from what ran a model. A NEW id, the SAME result.
        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(outputAssetId, second.Status.OutputAssetId);
        // A cache hit is never billed.
        Assert.Equal(before, await BalanceAsync());
    }

    /* ---------------- bring your own storage ---------------- */

    [Fact(DisplayName = "jobs.url-input")]
    public async Task JobsUrlInput()
    {
        var url = _fx.Url + "/__conformance/fixtures/small.png?nonce=" + Uri.EscapeDataString(Nonce("url-input"));

        // inputUrl is account-only.
        var refused = await _fx.Anonymous.SendAsync(
            "POST",
            "/jobs",
            "{\"operation\":\"remove-background\",\"inputUrl\":\"" + url + "\"}");
        Assert.Equal(403, refused.Status);
        Assert.Equal("forbidden", refused.ErrorCode);

        var created = await _fx.Api.CreateJobAsync(Operations.RemoveBackground, JobInput.FromUrl(url));
        Assert.Equal(202, created.HttpStatus);
        Assert.Equal(JobInputKind.Url, created.InputKind);

        // A url can be a bearer credential for someone's bucket; it is never echoed.
        var raw = await _fx.Authenticated.SendAsync("GET", "/jobs/" + created.JobId);
        Assert.DoesNotContain("nonce=", raw.Text, StringComparison.Ordinal);

        var done = await _fx.Api.WaitForJobAsync(created.JobId, FastWait());
        Assert.Equal(JobState.Succeeded, done.State);
        Assert.Equal(JobInputKind.Url, done.InputKind);
        Assert.NotNull(done.Status.Download);
    }

    [Fact(DisplayName = "jobs.url-input-fetch-failed")]
    public async Task JobsUrlInputFetchFailed()
    {
        var before = await BalanceAsync();

        // The url is only fetched by the worker, so creation succeeds.
        var created = await _fx.Api.CreateJobAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl(_fx.Url + "/__conformance/fixtures/does-not-exist.png"));
        Assert.Equal(202, created.HttpStatus);

        var done = await _fx.Api.WaitForJobAsync(created.JobId, FastWait());

        Assert.Equal(JobState.Failed, done.State);
        Assert.Equal(ErrorCode.InputFetchFailed, done.Status.ErrorCode);
        // Terminal, not retried — the url is the caller's to fix — and refunded.
        Assert.Equal(before, await BalanceAsync());

        // RunAsync surfaces the same failure as a typed exception.
        var error = await Assert.ThrowsAsync<SnapneditException>(() => _fx.Api.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl(_fx.Url + "/__conformance/fixtures/also-missing.png"),
            FastPoll()));
        Assert.Equal(ErrorCode.InputFetchFailed, error.Code);
    }

    [Fact(DisplayName = "jobs.presigned-destination")]
    public async Task JobsPresignedDestination()
    {
        var key = "presigned/" + Nonce("put") + ".png";
        var asset = await UniqueUploadAsync("presigned-dest");

        var run = await _fx.Api.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromAssetId(asset.AssetId),
            new RunOptions
            {
                Destination = JobDestination.PresignedPut(
                    _fx.Url + "/__conformance/bucket/" + key,
                    new Dictionary<string, string> { ["content-type"] = "image/png" }),
                PollInterval = TimeSpan.FromMilliseconds(50),
            });

        Assert.Equal("presigned-put", run.Destination!.Type);
        Assert.Null(run.Destination.Id);
        Assert.Equal(JobDeliveryStatus.Delivered, run.Delivery!.Status);
        Assert.True(run.Delivery.Attempts >= 1);
        Assert.NotNull(run.Delivery.DeliveredAt);
        // A presigned-put delivery does not remove our copy.
        Assert.NotNull(run.Download);
        // The bytes went straight to the bucket, so the download is skipped.
        Assert.False(run.Downloaded);

        // Neither the url nor the headers are ever echoed.
        var raw = await _fx.Authenticated.SendAsync("GET", "/jobs/" + run.JobId);
        Assert.DoesNotContain("__conformance/bucket", raw.Text, StringComparison.Ordinal);

        var stored = await BucketObjectAsync(key);
        Assert.Equal(200, stored.Status);
        Assert.True(stored.Json.GetProperty("bytes").GetInt32() > 0);
        Assert.Equal("image/png", stored.Json.GetProperty("contentType").GetString());
    }

    /* ---------------- saved destinations ---------------- */

    [Fact(DisplayName = "destinations.crud")]
    public async Task DestinationsCrud()
    {
        const string AccessKeyId = "CONFORMANCEKEYID";
        const string SecretAccessKey = "conformance-secret-access-key";

        var created = await _fx.Authenticated.SendAsync("POST", "/destinations", DestinationBody("crud/", AccessKeyId, SecretAccessKey));
        Assert.Equal(201, created.Status);
        var destination = created.Json.GetProperty("destination");
        var id = destination.GetProperty("id").GetString()!;
        foreach (var field in new[]
                 {
                     "id", "name", "provider", "bucket", "region", "endpoint", "forcePathStyle", "keyPrefix",
                     "accessKeyIdLast4", "isDefault", "deleteAfterDelivery", "lastTest", "createdAt", "updatedAt",
                 })
        {
            Assert.True(destination.TryGetProperty(field, out _), "missing field " + field);
        }

        Assert.Equal(AccessKeyId.Substring(AccessKeyId.Length - 4), destination.GetProperty("accessKeyIdLast4").GetString());
        Assert.DoesNotContain(SecretAccessKey, created.Text, StringComparison.Ordinal);

        var list = await _fx.Api.Destinations.ListAsync();
        var row = Assert.Single(list, d => d.Id == id);
        Assert.Equal(AccessKeyId.Substring(AccessKeyId.Length - 4), row.AccessKeyIdLast4);
        var listRaw = await _fx.Authenticated.SendAsync("GET", "/destinations");
        Assert.DoesNotContain(SecretAccessKey, listRaw.Text, StringComparison.Ordinal);

        var patched = await _fx.Api.Destinations.UpdateAsync(
            id,
            new StorageDestinationPatch { Name = "renamed by conformance" });
        Assert.Equal("renamed by conformance", patched.Name);

        var probe = await _fx.Api.Destinations.TestAsync(id);
        Assert.True(probe.Ok);
        Assert.Null(probe.Error);

        var removed = await _fx.Authenticated.SendAsync("DELETE", "/destinations/" + id);
        Assert.Equal(204, removed.Status);
        Assert.Equal(string.Empty, removed.Text);

        // 404, never 403 — for a deleted id and equally for another account's.
        foreach (var gone in new[] { id, "00000000-0000-4000-8000-000000000000" })
        {
            var error = await Assert.ThrowsAsync<SnapneditException>(
                () => _fx.Api.Destinations.UpdateAsync(gone, new StorageDestinationPatch { Name = "nope" }));
            Assert.Equal(ErrorCode.NotFound, error.Code);
            Assert.Equal(404, error.Status);
        }
    }

    [Fact(DisplayName = "destinations.presign")]
    public async Task DestinationsPresign()
    {
        var destination = await CreateDestinationAsync("exports/");
        try
        {
            var mismatched = await Assert.ThrowsAsync<SnapneditException>(
                () => _fx.Api.Destinations.PresignUploadAsync(destination.Id, DestinationExportExt.Png, "image/jpeg"));
            Assert.Equal(ErrorCode.InvalidInput, mismatched.Code);

            var signed = await _fx.Api.Destinations.PresignUploadAsync(destination.Id, DestinationExportExt.Png);

            Assert.Equal("PUT", signed.Method);
            Assert.Equal(_fx.Bucket, signed.Bucket);
            Assert.Matches(@"^exports/\d{4}/\d{2}/\d{2}/export-\d+-[a-z0-9]{6}\.png$", signed.Key);
            Assert.Equal("image/png", signed.Headers["content-type"]);
            Assert.True(DateTimeOffset.Parse(signed.ExpiresAt, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow);

            var payload = System.Text.Encoding.UTF8.GetBytes("conformance export bytes");
            var put = await _fx.Anonymous.SendBytesAsync("PUT", signed.Url, payload, signed.Headers);
            Assert.Equal(200, put.Status);

            var stored = await BucketObjectAsync(signed.Key);
            Assert.Equal(payload.Length, stored.Json.GetProperty("bytes").GetInt32());
        }
        finally
        {
            await _fx.Api.Destinations.DeleteAsync(destination.Id);
        }
    }

    [Fact(DisplayName = "jobs.saved-destination")]
    public async Task JobsSavedDestination()
    {
        var destination = await CreateDestinationAsync("saved/");
        try
        {
            // A destination id that is not on the caller's account is 404.
            var foreignAsset = await UniqueUploadAsync("saved-foreign");
            var foreign = await Assert.ThrowsAsync<SnapneditException>(() => _fx.Api.CreateJobAsync(
                Operations.RemoveBackground,
                JobInput.FromAssetId(foreignAsset.AssetId),
                new CreateJobOptions { Destination = JobDestination.Saved("00000000-0000-4000-8000-000000000000") }));
            Assert.Equal(ErrorCode.NotFound, foreign.Code);
            Assert.Equal(404, foreign.Status);

            var asset = await UniqueUploadAsync("saved-dest");
            var created = await _fx.Api.CreateJobAsync(
                Operations.RemoveBackground,
                JobInput.FromAssetId(asset.AssetId),
                new CreateJobOptions { Destination = JobDestination.Saved(destination.Id) });
            Assert.Equal(202, created.HttpStatus);

            var done = await _fx.Api.WaitForJobAsync(created.JobId, FastWait());

            Assert.Equal(JobState.Succeeded, done.State);
            // The id and label are the account's own, so they are safe to echo.
            Assert.Equal("saved", done.Destination!.Type);
            Assert.Equal(destination.Id, done.Destination.Id);
            Assert.Equal(JobDeliveryStatus.Delivered, done.Delivery!.Status);
            Assert.Equal(_fx.Bucket, done.Delivery.Bucket);
            Assert.Matches(@"^saved/\d{4}/\d{2}/\d{2}/" + Regex.Escape(created.JobId) + @"\.png$", done.Delivery.Key!);

            var stored = await BucketObjectAsync(done.Delivery.Key!);
            Assert.True(stored.Json.GetProperty("bytes").GetInt32() > 0);
        }
        finally
        {
            await _fx.Api.Destinations.DeleteAsync(destination.Id);
        }
    }

    [Fact(DisplayName = "jobs.default-destination-and-opt-out")]
    public async Task JobsDefaultDestinationAndOptOut()
    {
        var destination = await CreateDestinationAsync("default/", isDefault: true);
        try
        {
            // No destination key at all: the account default is applied.
            var inherited = await _fx.Api.CreateJobAsync(
                Operations.RemoveBackground,
                JobInput.FromAssetId((await UniqueUploadAsync("default-on")).AssetId));
            var inheritedDone = await _fx.Api.WaitForJobAsync(inherited.JobId, FastWait());
            Assert.Equal("saved", inheritedDone.Destination!.Type);
            Assert.Equal(destination.Id, inheritedDone.Destination.Id);
            Assert.Equal(JobDeliveryStatus.Delivered, inheritedDone.Delivery!.Status);
            Assert.StartsWith("default/", inheritedDone.Delivery.Key!, StringComparison.Ordinal);

            // An EXPLICIT null opts out — not the same as omitting the field.
            var optedOut = await _fx.Api.CreateJobAsync(
                Operations.RemoveBackground,
                JobInput.FromAssetId((await UniqueUploadAsync("default-off")).AssetId),
                new CreateJobOptions { Destination = JobDestination.OptOut });
            Assert.Null(optedOut.Destination);
            var optedOutDone = await _fx.Api.WaitForJobAsync(optedOut.JobId, FastWait());
            Assert.Null(optedOutDone.Destination);
            Assert.Null(optedOutDone.Delivery);
        }
        finally
        {
            await _fx.Api.Destinations.DeleteAsync(destination.Id);
        }
    }

    /* ---------------- masks, errors, ownership ---------------- */

    [Fact(DisplayName = "jobs.mask-required")]
    public async Task JobsMaskRequired()
    {
        var asset = await UniqueUploadAsync("mask-missing");

        var error = await Assert.ThrowsAsync<SnapneditException>(
            () => _fx.Api.CreateJobAsync(Operations.MagicEraser, JobInput.FromAssetId(asset.AssetId)));
        Assert.Equal(ErrorCode.InvalidInput, error.Code);
        Assert.Contains("maskAssetId", error.Message, StringComparison.Ordinal);

        // requiresMask is how an SDK knows to demand a mask before calling.
        var catalog = await _fx.Api.ListOperationsAsync();
        Assert.True(catalog.Single(o => o.Id == Operations.MagicEraser).RequiresMask);

        var mask = await UniqueUploadAsync("mask-bytes");
        var accepted = await _fx.Api.CreateJobAsync(
            Operations.MagicEraser,
            JobInput.FromAssetId(asset.AssetId),
            new CreateJobOptions { TypedParams = new MagicEraserParams { MaskAssetId = mask.AssetId } });
        Assert.Equal(202, accepted.HttpStatus);

        // RunAsync uploads the mask and fills maskAssetId in itself.
        var run = await _fx.Api.RunAsync(
            Operations.MagicEraser,
            JobInput.FromBytes(await FixtureAsync(Nonce("mask-run")), "image/png"),
            new RunOptions
            {
                Mask = JobInput.FromBytes(await FixtureAsync(Nonce("mask-run-mask")), "image/png"),
                PollInterval = TimeSpan.FromMilliseconds(50),
            });
        Assert.True(run.Downloaded);
    }

    [Fact(DisplayName = "jobs.foreign-job-is-404")]
    public async Task JobsForeignJobIs404()
    {
        var asset = await UniqueUploadAsync("ownership");
        var created = await _fx.Api.CreateJobAsync(Operations.RemoveBackground, JobInput.FromAssetId(asset.AssetId));

        var mine = await _fx.Api.GetJobAsync(created.JobId);
        Assert.Equal(created.JobId, mine.JobId);

        var anonymous = await _fx.Anonymous.SendAsync("GET", "/jobs/" + created.JobId);
        Assert.Equal(404, anonymous.Status);
        Assert.Equal("not_found", anonymous.ErrorCode);

        var wrongKey = await _fx.Anonymous.SendAsync(
            "GET",
            "/jobs/" + created.JobId,
            authorization: "Bearer sk_live_some_other_account");
        Assert.Equal(404, wrongKey.Status);

        // Indistinguishable from a job id that never existed — an id must not
        // become an existence oracle. (The human message names the id it was
        // given; the machine-readable code and status are what must match.)
        var neverExisted = await _fx.Anonymous.SendAsync("GET", "/jobs/00000000-0000-4000-8000-000000000000");
        Assert.Equal(404, neverExisted.Status);
        Assert.Equal(anonymous.ErrorCode, neverExisted.ErrorCode);
        Assert.Equal(TopLevelKeys(anonymous.Json), TopLevelKeys(neverExisted.Json));
    }

    /* ---------------- usage ---------------- */

    [Fact(DisplayName = "usage.query")]
    public async Task UsageQuery()
    {
        // Two real jobs, one paid and one free, so the assertions below are
        // about traffic this scenario produced. Unique bytes: a cache hit is
        // free and would never move `credits`.
        var paidAsset = await UniqueUploadAsync("usage-paid");
        var paid = await _fx.Api.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId(paidAsset.AssetId));
        Assert.Equal(202, paid.HttpStatus);
        Assert.Equal(JobState.Succeeded, (await _fx.Api.WaitForJobAsync(paid.JobId, FastWait())).State);

        var freeAsset = await UniqueUploadAsync("usage-free");
        var free = await _fx.Api.CreateJobAsync(
            Operations.ResizeImage,
            JobInput.FromAssetId(freeAsset.AssetId),
            new CreateJobOptions { TypedParams = new ResizeImageParams { Width = 4 } });
        Assert.Equal(202, free.HttpStatus);
        Assert.Equal(JobState.Succeeded, (await _fx.Api.WaitForJobAsync(free.JobId, FastWait())).State);

        var byOperation = await _fx.Api.GetUsageAsync(new UsageQuery { GroupBy = UsageGroupBy.Operation });

        Assert.True(DateTimeOffset.TryParse(
            byOperation.Range.From,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var rangeFrom));
        Assert.True(DateTimeOffset.TryParse(
            byOperation.Range.To,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var rangeTo));
        Assert.True(rangeFrom <= rangeTo);
        Assert.Equal(UsageGroupBy.Operation, byOperation.GroupBy);

        // Every scenario shares one seeded account and they run in any order,
        // so these are LOWER BOUNDS — never equalities.
        Assert.True(byOperation.Totals.Jobs >= 2, "totals.jobs was " + byOperation.Totals.Jobs);
        Assert.True(byOperation.Totals.Credits >= 2, "totals.credits was " + byOperation.Totals.Credits);
        Assert.True(byOperation.Totals.Free >= 1, "totals.free was " + byOperation.Totals.Free);
        foreach (var counter in new[]
                 {
                     byOperation.Totals.CacheHits, byOperation.Totals.Failed, byOperation.Totals.Delivered,
                     byOperation.Totals.DeliveryFailed, byOperation.Totals.Sessions,
                     byOperation.Totals.ActiveSessions,
                 })
        {
            Assert.True(counter >= 0);
        }

        var upscale = byOperation.Bucket("upscale");
        var resize = byOperation.Bucket("resize-image");
        Assert.NotNull(upscale);
        Assert.NotNull(resize);
        // A bucket's credits is jobs x cost, so with N upscales the honest
        // assertion is "a multiple of 2, at least 2".
        Assert.True(upscale!.Credits >= 2, "upscale credits was " + upscale.Credits);
        Assert.Equal(0, upscale.Credits % 2);
        Assert.Equal(0, resize!.Credits);
        foreach (var entry in byOperation.Series)
        {
            Assert.False(string.IsNullOrEmpty(entry.Key));
            Assert.False(string.IsNullOrEmpty(entry.Label));
            Assert.True(entry.Jobs >= 0 && entry.Credits >= 0 && entry.CacheHits >= 0 && entry.Free >= 0);
            Assert.True(entry.Failed >= 0 && entry.Delivered >= 0 && entry.DeliveryFailed >= 0 && entry.Sessions >= 0);
        }

        // The cap gauge: an `sk_` caller sees every live key on the account.
        Assert.True(byOperation.Keys.Count >= 2, "expected the seeded account's two keys");
        foreach (var key in byOperation.Keys)
        {
            Assert.False(string.IsNullOrEmpty(key.Id));
            Assert.False(string.IsNullOrEmpty(key.Name));
            Assert.Contains(key.Kind, new[] { "secret", "publishable" });
            Assert.True(key.UsedToday >= 0);
            Assert.Equal(key.DailyCreditLimit.HasValue, key.IsCapped);
        }

        var byDay = await _fx.Api.GetUsageAsync(new UsageQuery { GroupBy = UsageGroupBy.Day });
        Assert.Equal(UsageGroupBy.Day, byDay.GroupBy);
        Assert.Contains(
            DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            byDay.Series.Select(entry => entry.Key));

        var unauthorized = await _fx.Anonymous.SendAsync("GET", "/usage");
        Assert.Equal(401, unauthorized.Status);
        Assert.Equal("unauthorized", unauthorized.ErrorCode);

        var backwards = await Assert.ThrowsAsync<SnapneditException>(() => _fx.Api.GetUsageAsync(new UsageQuery
        {
            From = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        }));
        Assert.Equal(400, backwards.Status);
        Assert.Equal(ErrorCode.InvalidInput, backwards.Code);
    }

    [Fact(DisplayName = "errors.envelope")]
    public async Task ErrorsEnvelope()
    {
        var responses = new[]
        {
            await _fx.Authenticated.SendAsync("POST", "/jobs", "{}"),
            await _fx.Authenticated.SendAsync("GET", "/jobs/00000000-0000-4000-8000-000000000000"),
            await _fx.Anonymous.SendAsync("GET", "/destinations"),
            await _fx.Authenticated.SendAsync("POST", "/uploads", "{\"mime\":\"application/pdf\",\"bytes\":10}"),
        };

        Assert.Equal(new[] { 400, 404, 401, 400 }, responses.Select(r => r.Status).ToArray());
        var codes = ErrorCodes.All.Select(ErrorCodes.ToWire).ToArray();
        foreach (var response in responses)
        {
            Assert.Equal(new[] { "error" }, TopLevelKeys(response.Json));
            Assert.Equal(
                new[] { "code", "message" },
                response.Json.GetProperty("error").EnumerateObject()
                    .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Contains(response.ErrorCode!, codes);
            Assert.NotEmpty(response.ErrorMessage!);
        }
    }

    [Fact(DisplayName = "uploads.unsupported-mime")]
    public async Task UploadsUnsupportedMime()
    {
        var error = await Assert.ThrowsAsync<SnapneditException>(
            () => _fx.Api.UploadAsync(new byte[1024], "application/pdf"));
        Assert.Equal(ErrorCode.UnsupportedMime, error.Code);
        Assert.Equal(400, error.Status);

        var operations = await _fx.Api.ListOperationsAsync();
        Assert.Equal(
            new[] { "image/jpeg", "image/png", "image/webp" },
            operations.SelectMany(o => o.Accept).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }

    /* ---------------- embed ---------------- */

    [Fact(DisplayName = "embed.session")]
    public async Task EmbedSession()
    {
        var allowed = await _fx.Api.Embed.CreateSessionAsync(_fx.PublishableKey, "http://localhost:3000");
        Assert.NotEmpty(allowed.Token);
        Assert.True(DateTimeOffset.Parse(allowed.ExpiresAt, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow);

        var refused = await Assert.ThrowsAsync<SnapneditException>(
            () => _fx.Api.Embed.CreateSessionAsync(_fx.PublishableKey, "https://not-allowed.example"));
        Assert.Equal(ErrorCode.Forbidden, refused.Code);
        Assert.Equal(403, refused.Status);

        var unauthenticated = await _fx.Anonymous.SendAsync("POST", "/embed/tokens", "{\"ttlSeconds\":600}");
        Assert.Equal(401, unauthenticated.Status);
        Assert.Equal("unauthorized", unauthenticated.ErrorCode);

        var minted = await _fx.Api.Embed.CreateTokenAsync(new EmbedTokenOptions { TtlSeconds = 600 });
        Assert.NotEmpty(minted.Token);

        // The token authenticates as the account.
        using var asEmbed = _fx.NewClient(minted.Token);
        var destinations = await asEmbed.Destinations.ListAsync();
        Assert.NotNull(destinations);
    }

    /* ---------------- webhooks ---------------- */

    [Fact(DisplayName = "webhooks.signature")]
    public void WebhooksSignature()
    {
        var vector = _fx.Scenarios.GetProperty("webhookVector");
        var secret = vector.GetProperty("secret").GetString()!;
        var timestamp = vector.GetProperty("timestamp").GetInt64();
        var rawBody = vector.GetProperty("rawBody").GetString()!;
        var signature = vector.GetProperty("signature").GetString()!;
        var header = vector.GetProperty("signatureHeaderValue").GetString()!;

        Assert.Equal(signature, SnapneditWebhooks.ComputeSignature(timestamp, rawBody, secret));
        Assert.Equal("t=" + timestamp.ToString(CultureInfo.InvariantCulture) + ",v1=" + signature, header);

        Assert.True(SnapneditWebhooks.Verify(rawBody, header, secret));

        var flipped = signature.EndsWith("a", StringComparison.Ordinal) ? 'b' : 'a';
        var tampered = "t=" + timestamp.ToString(CultureInfo.InvariantCulture) + ",v1=" +
                       signature.Substring(0, signature.Length - 1) + flipped;
        Assert.False(SnapneditWebhooks.Verify(rawBody, tampered, secret));
        Assert.False(SnapneditWebhooks.Verify(rawBody, header, "wrong secret"));
        // The RAW body is what is signed — a re-serialized one will not match.
        Assert.False(SnapneditWebhooks.Verify(rawBody + " ", header, secret));
        Assert.False(SnapneditWebhooks.Verify(rawBody, "not a signature header", secret));
        Assert.False(SnapneditWebhooks.Verify(rawBody, "v1=" + signature, secret));
        Assert.False(SnapneditWebhooks.Verify(rawBody, "t=" + timestamp.ToString(CultureInfo.InvariantCulture), secret));

        Assert.True(SnapneditWebhooks.Verify(
            rawBody,
            header,
            secret,
            new WebhookVerifyOptions { ToleranceSeconds = 300, NowUnixSeconds = timestamp + 10 }));
        Assert.False(SnapneditWebhooks.Verify(
            rawBody,
            header,
            secret,
            new WebhookVerifyOptions { ToleranceSeconds = 300, NowUnixSeconds = timestamp + 3600 }));

        // The server's live secret is the same fixed vector secret.
        Assert.Equal(secret, _fx.WebhookSecret);
    }

    /* ---------------- meta ---------------- */

    [Fact(DisplayName = "openapi.document")]
    public async Task OpenapiDocument()
    {
        var response = await _fx.Anonymous.SendAsync("GET", "/openapi.json");

        Assert.Equal(200, response.Status);
        Assert.Contains("application/json", response.ContentType!, StringComparison.Ordinal);
        Assert.Equal("3.1.0", response.Json.GetProperty("openapi").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", response.Json.GetProperty("info").GetProperty("version").GetString()!);

        var paths = response.Json.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var path in new[]
                 {
                     "/operations", "/uploads", "/uploads/{assetId}/confirm", "/jobs", "/jobs/{id}",
                     "/destinations", "/destinations/{id}", "/destinations/{id}/test", "/destinations/{id}/presign",
                     "/embed/sessions", "/embed/tokens",
                 })
        {
            Assert.Contains(path, paths);
        }

        Assert.Equal(
            new[] { "ApiKey", "EmbedToken", "Session" },
            response.Json.GetProperty("components").GetProperty("securitySchemes").EnumerateObject()
                .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            0,
            response.Json.GetProperty("components").GetProperty("schemas")
                .GetProperty("OperationParams.resize-image").GetProperty("x-credit-cost").GetInt32());

        var committed = File.ReadAllText(Path.Combine(ConformanceServerFixture.RepoRoot!, "docs", "openapi.json"));
        Assert.Equal(committed, response.Text);
    }

    /* ---------------- coverage ---------------- */

    [Fact(DisplayName = "every scenario in scenarios.json is implemented here")]
    public void EveryScenarioIsImplemented()
    {
        var declared = _fx.Scenarios.GetProperty("scenarios").EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var implemented = typeof(ConformanceTests)
            .GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(FactAttribute), false).Cast<FactAttribute>())
            .Select(f => f.DisplayName)
            .Where(name => name is not null && declared.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(declared, implemented);
        Assert.Equal(25, declared.Length);
        Assert.Equal(1, _fx.Scenarios.GetProperty("version").GetInt32());
    }

    /* ---------------- helpers ---------------- */

    private static string Nonce(string label) => label + "-" + Guid.NewGuid().ToString("N");

    private static RunOptions FastPoll() => new RunOptions { PollInterval = TimeSpan.FromMilliseconds(50) };

    private static WaitForJobOptions FastWait() =>
        new WaitForJobOptions { PollInterval = TimeSpan.FromMilliseconds(50), Timeout = TimeSpan.FromSeconds(60) };

    private static string[] TopLevelKeys(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();

    /// <summary>Real image bytes nobody has submitted before, so the request cannot be a cache hit.</summary>
    private async Task<byte[]> FixtureAsync(string nonce)
    {
        var fetched = await _fx.Anonymous.GetBytesAsync(
            "/__conformance/fixtures/small.png?nonce=" + Uri.EscapeDataString(nonce));
        Assert.Equal(200, fetched.Status);
        return fetched.Bytes;
    }

    private async Task<UploadResult> UniqueUploadAsync(string label) =>
        await _fx.Api.UploadAsync(await FixtureAsync(Nonce(label)), "image/png");

    /// <summary>The seeded account's live balance. Always assert on DELTAS: scenarios share one account.</summary>
    private async Task<int> BalanceAsync()
    {
        var response = await _fx.Anonymous.SendAsync("GET", "/__conformance/credits");
        Assert.Equal(200, response.Status);
        return response.Json.GetProperty("balance").GetInt32();
    }

    private Task<RestResponse> BucketObjectAsync(string key) =>
        _fx.Anonymous.SendAsync("GET", "/__conformance/bucket/" + key);

    private string DestinationBody(string keyPrefix, string accessKeyId, string secretAccessKey) =>
        "{\"name\":\"conformance " + Nonce("dest") + "\",\"provider\":\"s3-compatible\",\"bucket\":\"" + _fx.Bucket +
        "\",\"region\":\"auto\",\"endpoint\":\"" + _fx.Url + "\",\"forcePathStyle\":true,\"keyPrefix\":\"" + keyPrefix +
        "\",\"accessKeyId\":\"" + accessKeyId + "\",\"secretAccessKey\":\"" + secretAccessKey + "\"}";

    private Task<StorageDestination> CreateDestinationAsync(string keyPrefix, bool isDefault = false) =>
        _fx.Api.Destinations.CreateAsync(new StorageDestinationInput(
            "conformance " + Nonce("dest"),
            StorageProvider.S3Compatible,
            _fx.Bucket,
            "CONFORMANCEKEYID",
            "conformance-secret-access-key")
        {
            Region = "auto",
            Endpoint = _fx.Url,
            ForcePathStyle = true,
            KeyPrefix = keyPrefix,
            IsDefault = isDefault,
        });

    /// <summary>Polls <c>GET /jobs/{id}</c> with plain HTTP until the state is terminal.</summary>
    private async Task<JsonElement> PollRawAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var response = await _fx.Authenticated.SendAsync("GET", "/jobs/" + jobId);
            Assert.Equal(200, response.Status);
            var state = response.Json.GetProperty("state").GetString();
            if (state is "succeeded" or "failed" or "canceled")
            {
                return response.Json;
            }

            Assert.True(DateTime.UtcNow < deadline, "job " + jobId + " never reached a terminal state");
            await Task.Delay(50);
        }
    }
}
