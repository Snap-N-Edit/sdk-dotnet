using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Snapnedit.Tests;

/// <summary>Unit tests for the client's wire behaviour, driven entirely by a fake handler.</summary>
public sealed class ClientTests
{
    private const string UploadCreated = "{\"assetId\":\"asset-1\",\"upload\":{\"url\":\"/_local/uploads/asset-1?sig=x\",\"expiresAt\":\"2026-01-01T00:00:00.000Z\"}}";
    private const string UploadConfirmed = "{\"assetId\":\"asset-1\",\"contentHash\":\"" + Sha + "\",\"bytes\":8}";
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static SnapneditClient Build(FakeHttpMessageHandler handler) =>
        new SnapneditClient(
            "sk_live_test",
            new SnapneditClientOptions
            {
                BaseUrl = "https://api.example.test",
                HttpClient = new HttpClient(handler),
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            });

    [Fact]
    public async Task UploadAsync_creates_puts_then_confirms()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
            FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed));
        using var client = Build(handler);

        var result = await client.UploadAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "image/png");

        Assert.Equal("asset-1", result.AssetId);
        Assert.Equal(Sha, result.ContentHash);
        Assert.Equal(8, result.Bytes);
        Assert.Equal(
            new[]
            {
                "https://api.example.test/uploads",
                "https://api.example.test/_local/uploads/asset-1?sig=x",
                "https://api.example.test/uploads/asset-1/confirm",
            },
            handler.Urls);
        Assert.Equal("{\"mime\":\"image/png\",\"bytes\":8}", handler.Requests[0].Body);
        // A presigned url is self-authenticating: a second Authorization header
        // is what S3 SigV4 rejects outright.
        Assert.Null(handler.Requests[1].Authorization);
        Assert.Equal("Bearer sk_live_test", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task CreateJobAsync_sends_inputAssetId_and_params()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"job-1\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null," +
            "\"creditCost\":2,\"cached\":false,\"deliveryOnly\":false}"));
        using var client = Build(handler);

        var created = await client.CreateJobAsync(
            Operations.Upscale,
            JobInput.FromAssetId("asset-9"),
            new CreateJobOptions { TypedParams = new UpscaleParams { Factor = UpscaleFactor.X4 } });

        Assert.Equal("job-1", created.JobId);
        Assert.Equal(JobState.Queued, created.Status.State);
        Assert.Equal(202, created.HttpStatus);
        Assert.False(created.WasCacheHit);
        Assert.Equal(JobInputKind.Asset, created.InputKind);
        // The usage attribution the api now echoes on every job response.
        Assert.Equal(2, created.CreditCost);
        Assert.False(created.Cached);
        Assert.False(created.DeliveryOnly);
        Assert.Equal(
            "{\"operation\":\"upscale\",\"inputAssetId\":\"asset-9\",\"params\":{\"factor\":\"4\"}}",
            handler.Requests[0].Body);
    }

    [Fact]
    public async Task CreateJobAsync_sends_inputUrl_for_a_url_input()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"job-2\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"url\"},\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        var created = await client.CreateJobAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl("https://cdn.example.com/a.png"));

        Assert.Equal(JobInputKind.Url, created.InputKind);
        Assert.Contains("\"inputUrl\":\"https://cdn.example.com/a.png\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("inputAssetId", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Destination_omitted_null_and_saved_are_three_different_bodies()
    {
        const string Accepted = "{\"jobId\":\"j\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}";
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(HttpStatusCode.Accepted, Accepted));
        using var client = Build(handler);
        var input = JobInput.FromAssetId("a");

        await client.CreateJobAsync(Operations.RemoveBackground, input);
        await client.CreateJobAsync(Operations.RemoveBackground, input, new CreateJobOptions { Destination = JobDestination.OptOut });
        await client.CreateJobAsync(Operations.RemoveBackground, input, new CreateJobOptions { Destination = JobDestination.Saved("dest-1") });
        await client.CreateJobAsync(
            Operations.RemoveBackground,
            input,
            new CreateJobOptions
            {
                Destination = JobDestination.PresignedPut(
                    "https://bucket.example/x.png",
                    new Dictionary<string, string> { ["content-type"] = "image/png" }),
            });

        // Omitted means "apply my account default"; an explicit null opts out.
        Assert.DoesNotContain("destination", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"destination\":null", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("\"destination\":{\"type\":\"saved\",\"id\":\"dest-1\"}", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains(
            "\"destination\":{\"type\":\"presigned-put\",\"url\":\"https://bucket.example/x.png\",\"headers\":{\"content-type\":\"image/png\"}}",
            handler.Requests[3].Body,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_uploads_polls_and_downloads()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
            FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                "{\"jobId\":\"job-7\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"processing\",\"startedAt\":\"2026-01-01T00:00:00.000Z\",\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"succeeded\",\"outputAssetId\":\"out-1\",\"download\":{\"url\":\"/_local/results/out-1.png?sig=y\",\"expiresAt\":\"2026-01-01T01:00:00.000Z\"},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"),
            FakeHttpMessageHandler.Binary(HttpStatusCode.OK, png, "image/png"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "image/png"),
            new RunOptions { PollInterval = TimeSpan.FromMilliseconds(1) });

        Assert.True(result.Downloaded);
        Assert.Equal(png, result.Output);
        Assert.Equal("image/png", result.Mime);
        Assert.Equal("out-1", result.OutputAssetId);
        Assert.Equal("https://api.example.test/_local/results/out-1.png?sig=y", handler.Urls[^1]);
    }

    [Fact]
    public async Task RunAsync_skips_the_download_when_an_explicit_destination_was_delivered()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"job-8\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"out-2\",\"download\":{\"url\":\"/_local/r.png\",\"expiresAt\":\"z\"}}," +
            "\"input\":{\"kind\":\"url\"},\"destination\":{\"type\":\"presigned-put\"},\"delivery\":{\"status\":\"delivered\",\"attempts\":1,\"deliveredAt\":\"2026-01-01T00:00:00.000Z\"}}"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl("https://cdn.example.com/a.png"),
            new RunOptions { Destination = JobDestination.PresignedPut("https://bucket.example/x.png") });

        Assert.False(result.Downloaded);
        Assert.Null(result.Output);
        Assert.NotNull(result.Download);
        Assert.Equal(JobDeliveryStatus.Delivered, result.Delivery!.Status);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RunAsync_still_downloads_when_the_delivery_failed()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                "{\"jobId\":\"job-9\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"out-3\",\"download\":{\"url\":\"/_local/r.png\",\"expiresAt\":\"z\"}}," +
                "\"input\":{\"kind\":\"url\"},\"destination\":{\"type\":\"presigned-put\"},\"delivery\":{\"status\":\"failed\",\"attempts\":3,\"error\":\"403\"}}"),
            FakeHttpMessageHandler.Binary(HttpStatusCode.OK, new byte[] { 9 }, "image/png"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl("https://cdn.example.com/a.png"),
            new RunOptions { Destination = JobDestination.PresignedPut("https://bucket.example/x.png") });

        // A failed delivery does not fail the job, and must not leave the
        // caller empty-handed.
        Assert.True(result.Downloaded);
        Assert.Equal(JobDeliveryStatus.Failed, result.Delivery!.Status);
    }

    [Fact]
    public async Task RunAsync_polls_a_cache_hit_whose_delivery_is_still_pending()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"jobId\":\"job-10\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"out-4\",\"download\":{\"url\":\"/_local/r.png\",\"expiresAt\":\"z\"}}," +
                "\"input\":{\"kind\":\"asset\"},\"destination\":{\"type\":\"saved\",\"id\":\"d1\"},\"delivery\":{\"status\":\"pending\",\"attempts\":0}}"),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"succeeded\",\"outputAssetId\":\"out-4\",\"download\":{\"url\":\"/_local/r.png\",\"expiresAt\":\"z\"}," +
                "\"input\":{\"kind\":\"asset\"},\"destination\":{\"type\":\"saved\",\"id\":\"d1\",\"name\":\"bucket\"},\"delivery\":{\"status\":\"delivered\",\"attempts\":1,\"key\":\"k.png\",\"bucket\":\"b\"}}"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromAssetId("a"),
            new RunOptions { Destination = JobDestination.Saved("d1"), PollInterval = TimeSpan.FromMilliseconds(1) });

        Assert.Equal(2, handler.Requests.Count);
        Assert.False(result.Downloaded);
        Assert.Equal("k.png", result.Delivery!.Key);
    }

    [Fact]
    public async Task RunAsync_reports_a_deleted_local_copy_as_not_downloaded_rather_than_an_error()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"job-11\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"out-5\",\"download\":null}," +
            "\"input\":{\"kind\":\"asset\"},\"destination\":{\"type\":\"saved\",\"id\":\"d1\"},\"delivery\":{\"status\":\"delivered\",\"attempts\":1,\"localCopyDeleted\":true}}"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromAssetId("a"),
            new RunOptions { Destination = JobDestination.Saved("d1"), Download = true });

        Assert.False(result.Downloaded);
        Assert.Null(result.Download);
        Assert.True(result.Delivery!.LocalCopyDeleted);
    }

    [Fact]
    public async Task RunAsync_throws_the_jobs_own_error_code_when_it_fails()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                "{\"jobId\":\"job-12\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"url\"},\"destination\":null,\"delivery\":null}"),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"failed\",\"errorCode\":\"input_fetch_failed\",\"message\":\"could not fetch\",\"input\":{\"kind\":\"url\"},\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        var error = await Assert.ThrowsAsync<SnapneditException>(() => client.RunAsync(
            Operations.RemoveBackground,
            JobInput.FromUrl("https://cdn.example.com/missing.png"),
            new RunOptions { PollInterval = TimeSpan.FromMilliseconds(1) }));

        Assert.Equal(ErrorCode.InputFetchFailed, error.Code);
        Assert.Equal("could not fetch", error.Message);
    }

    [Fact]
    public async Task RunAsync_uploads_a_mask_and_sets_maskAssetId()
    {
        var handler = new FakeHttpMessageHandler((request, index) => index switch
        {
            0 or 3 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
            1 or 4 => FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
            2 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed),
            5 => FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"assetId\":\"mask-1\",\"contentHash\":\"" + Sha + "\",\"bytes\":4}"),
            _ => FakeHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                "{\"jobId\":\"job-13\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"o\",\"download\":null},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"),
        });
        using var client = Build(handler);

        await client.RunAsync(
            Operations.MagicEraser,
            JobInput.FromBytes(new byte[] { 1, 2, 3, 4 }, "image/png"),
            new RunOptions { Mask = JobInput.FromBytes(new byte[] { 5, 6, 7, 8 }, "image/png") });

        var jobBody = handler.Requests[^1].Body;
        Assert.Contains("\"maskAssetId\":\"mask-1\"", jobBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_error_envelope_becomes_a_typed_exception()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.PaymentRequired,
            "{\"error\":{\"code\":\"payment_required\",\"message\":\"out of credits\"}}"));
        using var client = Build(handler);

        var error = await Assert.ThrowsAsync<SnapneditException>(
            () => client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId("a")));

        Assert.Equal(ErrorCode.PaymentRequired, error.Code);
        Assert.Equal(402, error.Status);
        Assert.Equal("out of credits", error.Message);
    }

    [Fact]
    public async Task An_unknown_error_code_degrades_to_internal_instead_of_throwing_while_parsing()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"some_future_code\",\"message\":\"nope\"}}"));
        using var client = Build(handler);

        var error = await Assert.ThrowsAsync<SnapneditException>(
            () => client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId("a")));

        Assert.Equal(ErrorCode.Internal, error.Code);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task An_idempotent_GET_is_retried_on_a_500()
    {
        var handler = new FakeHttpMessageHandler((_, index) => index < 2
            ? FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"internal\",\"message\":\"boom\"}}")
            : FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"queued\",\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        var view = await client.GetJobAsync("job-14");

        Assert.Equal(JobState.Queued, view.State);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_POST_is_never_retried_because_it_may_already_have_debited_credits()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            FakeHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"code\":\"internal\",\"message\":\"down\"}}"));
        using var client = Build(handler);

        await Assert.ThrowsAsync<SnapneditException>(
            () => client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId("a")));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task WaitForJobAsync_gives_up_with_a_timeout_exception()
    {
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"state\":\"queued\",\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        await Assert.ThrowsAsync<SnapneditTimeoutException>(() => client.WaitForJobAsync(
            "job-15",
            new WaitForJobOptions { PollInterval = TimeSpan.FromMilliseconds(1), Timeout = TimeSpan.FromMilliseconds(20) }));
    }

    [Fact]
    public async Task ListOperationsAsync_reads_the_catalog()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "[{\"id\":\"upscale\",\"label\":\"Upscale\",\"accept\":[\"image/png\"],\"maxInputDimension\":4096," +
            "\"paramsJsonSchema\":{\"properties\":{\"factor\":{\"enum\":[\"2\",\"4\"]}}},\"requiresMask\":false," +
            "\"description\":\"d\",\"seoTitle\":\"s\"}]"));
        using var client = Build(handler);

        var operations = await client.ListOperationsAsync();

        var upscale = Assert.Single(operations);
        Assert.Equal("upscale", upscale.Id);
        Assert.Equal(4096, upscale.MaxInputDimension);
        Assert.False(upscale.RequiresMask);
        // The schema element survives the JsonDocument it was parsed from.
        Assert.Equal(
            JsonValueKind.Object,
            upscale.ParamsJsonSchema.GetProperty("properties").ValueKind);
    }

    [Fact]
    public async Task Destinations_round_trip_through_their_envelope()
    {
        const string Destination = "{\"destination\":{\"id\":\"d1\",\"name\":\"bucket\",\"provider\":\"cloudflare-r2\",\"bucket\":\"b\"," +
                                   "\"region\":\"auto\",\"endpoint\":null,\"forcePathStyle\":true,\"keyPrefix\":\"p/\"," +
                                   "\"accessKeyIdLast4\":\"WXYZ\",\"isDefault\":false,\"deleteAfterDelivery\":false," +
                                   "\"lastTest\":null,\"createdAt\":\"t\",\"updatedAt\":\"t\"}}";
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.Created, Destination),
            FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent));
        using var client = Build(handler);

        var created = await client.Destinations.CreateAsync(new StorageDestinationInput(
            "bucket",
            StorageProvider.CloudflareR2,
            "b",
            "ABCDWXYZ",
            "secret")
        {
            KeyPrefix = "p/",
            ForcePathStyle = true,
        });
        await client.Destinations.DeleteAsync("d1");

        Assert.Equal("d1", created.Id);
        Assert.Equal(StorageProvider.CloudflareR2, created.Provider);
        Assert.Equal("WXYZ", created.AccessKeyIdLast4);
        Assert.Null(created.Endpoint);
        Assert.Contains("\"secretAccessKey\":\"secret\"", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("DELETE", handler.Requests[1].Method);
    }

    [Fact]
    public async Task A_patch_can_clear_a_nullable_field()
    {
        const string Destination = "{\"destination\":{\"id\":\"d1\",\"name\":\"n\",\"provider\":\"aws-s3\",\"bucket\":\"b\"," +
                                   "\"region\":null,\"endpoint\":null,\"forcePathStyle\":false,\"keyPrefix\":\"\"," +
                                   "\"accessKeyIdLast4\":\"1234\",\"isDefault\":true,\"deleteAfterDelivery\":true," +
                                   "\"lastTest\":{\"status\":\"ok\",\"at\":\"t\"},\"createdAt\":\"t\",\"updatedAt\":\"t\"}}";
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(HttpStatusCode.OK, Destination));
        using var client = Build(handler);

        var updated = await client.Destinations.UpdateAsync(
            "d1",
            new StorageDestinationPatch { Name = "n", ClearEndpoint = true });

        Assert.Equal("PATCH", handler.Requests[0].Method);
        Assert.Equal("{\"name\":\"n\",\"endpoint\":null}", handler.Requests[0].Body);
        Assert.True(updated.LastTest!.Ok);
        Assert.True(updated.IsDefault);
    }

    [Fact]
    public async Task An_embed_session_is_sent_without_the_clients_own_key()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"token\":\"tok\",\"expiresAt\":\"2026-01-01T00:00:00.000Z\"}"),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{\"token\":\"tok2\",\"expiresAt\":\"2026-01-01T00:00:00.000Z\"}"));
        using var client = Build(handler);

        var session = await client.Embed.CreateSessionAsync("pk_live_x", "https://app.example.com");
        var minted = await client.Embed.CreateTokenAsync(new EmbedTokenOptions
        {
            TtlSeconds = 600,
            MaxCredits = 20,
            AllowedOperations = new[] { Operations.RemoveBackground },
        });

        Assert.Equal("tok", session.Token);
        Assert.Null(handler.Requests[0].Authorization);
        Assert.Equal("tok2", minted.Token);
        Assert.Equal("Bearer sk_live_test", handler.Requests[1].Authorization);
        Assert.Equal(
            "{\"ttlSeconds\":600,\"maxCredits\":20,\"allowedOperations\":[\"remove-background\"]}",
            handler.Requests[1].Body);
    }

    [Fact]
    public async Task JobInput_FromFile_infers_the_mime_from_the_extension()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        try
        {
            var handler = new FakeHttpMessageHandler(
                FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
                FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
                FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed));
            using var client = Build(handler);

            await client.UploadAsync(JobInput.FromFile(path));

            Assert.Equal("{\"mime\":\"image/jpeg\",\"bytes\":3}", handler.Requests[0].Body);
            Assert.Equal("image/jpeg", handler.Requests[1].ContentType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JobInput_FromStream_buffers_the_stream()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
            FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed));
        using var client = Build(handler);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("not really a png"));

        await client.UploadAsync(JobInput.FromStream(stream, "image/png"));

        Assert.Equal("{\"mime\":\"image/png\",\"bytes\":16}", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Free_form_params_win_over_typed_ones()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"j\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        await client.CreateJobAsync(
            Operations.ResizeImage,
            JobInput.FromAssetId("a"),
            new CreateJobOptions
            {
                TypedParams = new ResizeImageParams { Width = 100, Format = ResizeFormat.Webp },
                Params = new Dictionary<string, object?> { ["width"] = 200 },
            });

        Assert.Contains("\"width\":200", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("\"format\":\"webp\"", handler.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_param_value_type_is_refused_with_a_message_naming_the_key()
    {
        var handler = new FakeHttpMessageHandler((_, _) => FakeHttpMessageHandler.Empty(HttpStatusCode.OK));
        using var client = Build(handler);

        var error = Assert.Throws<ArgumentException>(() => client.CreateJobAsync(
            Operations.Upscale,
            JobInput.FromAssetId("a"),
            new CreateJobOptions { Params = new Dictionary<string, object?> { ["oops"] = new object() } }).GetAwaiter().GetResult());

        Assert.Contains("params.oops", error.Message, StringComparison.Ordinal);
    }

    private const string UsageReportBody = "{\"range\":{\"from\":\"2026-08-14T00:00:00.000Z\",\"to\":\"2026-09-13T23:59:59.999Z\"},\"groupBy\":\"operation\",\"totals\":{\"jobs\":12,\"credits\":17,\"cacheHits\":3,\"free\":2,\"failed\":1,\"delivered\":4,\"deliveryFailed\":1,\"sessions\":5,\"activeSessions\":2},\"series\":[{\"key\":\"upscale\",\"label\":\"upscale\",\"jobs\":8,\"credits\":16,\"cacheHits\":2,\"free\":0,\"failed\":1,\"delivered\":4,\"deliveryFailed\":1,\"sessions\":0},{\"key\":\"resize-image\",\"label\":\"resize-image\",\"jobs\":4,\"credits\":0,\"cacheHits\":1,\"free\":2,\"failed\":0,\"delivered\":0,\"deliveryFailed\":0,\"sessions\":0}],\"keys\":[{\"id\":\"key-1\",\"name\":\"server\",\"kind\":\"secret\",\"dailyCreditLimit\":null,\"usedToday\":9},{\"id\":\"key-2\",\"name\":\"web\",\"kind\":\"publishable\",\"dailyCreditLimit\":50,\"usedToday\":12}]}";

    [Fact]
    public async Task GetUsageAsync_with_no_query_sends_no_query_string()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(HttpStatusCode.OK, UsageReportBody));
        using var client = Build(handler);

        await client.GetUsageAsync();

        Assert.Equal("https://api.example.test/usage", handler.Urls[0]);
        Assert.Equal("Bearer sk_live_test", handler.Requests[0].Authorization);
    }

    [Fact]
    public async Task GetUsageAsync_sends_only_the_filters_that_were_set()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(HttpStatusCode.OK, UsageReportBody));
        using var client = Build(handler);

        await client.GetUsageAsync(new UsageQuery
        {
            From = new DateTimeOffset(2026, 9, 1, 14, 30, 45, TimeSpan.FromHours(2)),
            To = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            GroupBy = UsageGroupBy.Key,
            KeyId = "key-2",
            Origin = "https://app.example.com",
            Source = UsageSource.Embed,
        });

        // The offset is normalized to UTC, and every value is percent-encoded.
        Assert.Equal(
            "https://api.example.test/usage?from=2026-09-01T12%3A30%3A45.000Z&to=2026-09-13T00%3A00%3A00.000Z" +
            "&groupBy=key&keyId=key-2&origin=https%3A%2F%2Fapp.example.com&source=embed",
            handler.Urls[0]);
    }

    [Fact]
    public async Task GetUsageAsync_parses_the_range_totals_series_and_key_gauge()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(HttpStatusCode.OK, UsageReportBody));
        using var client = Build(handler);

        var report = await client.GetUsageAsync(new UsageQuery { GroupBy = UsageGroupBy.Operation });

        Assert.Equal("2026-08-14T00:00:00.000Z", report.Range.From);
        Assert.Equal("2026-09-13T23:59:59.999Z", report.Range.To);
        Assert.Equal(UsageGroupBy.Operation, report.GroupBy);
        Assert.Equal(12, report.Totals.Jobs);
        Assert.Equal(17, report.Totals.Credits);
        Assert.Equal(3, report.Totals.CacheHits);
        Assert.Equal(5, report.Totals.Sessions);
        Assert.Equal(2, report.Totals.ActiveSessions);

        Assert.Equal(new[] { "upscale", "resize-image" }, report.Series.Select(entry => entry.Key).ToArray());
        Assert.Equal(16, report.Bucket("upscale")!.Credits);
        Assert.Equal(0, report.Bucket("resize-image")!.Credits);
        Assert.Null(report.Bucket("magic-eraser"));

        var uncapped = report.Keys[0];
        Assert.Equal("key-1", uncapped.Id);
        Assert.Equal("secret", uncapped.Kind);
        Assert.Null(uncapped.DailyCreditLimit);
        Assert.False(uncapped.IsCapped);
        Assert.Null(uncapped.RemainingToday);

        var capped = report.Keys[1];
        Assert.True(capped.IsCapped);
        Assert.Equal(38, capped.RemainingToday);
    }

    [Fact]
    public async Task GetUsageAsync_never_reports_a_negative_remaining_allowance()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"range\":{\"from\":\"2026-09-01T00:00:00.000Z\",\"to\":\"2026-09-13T00:00:00.000Z\"}," +
            "\"groupBy\":\"day\",\"totals\":{},\"series\":[]," +
            "\"keys\":[{\"id\":\"k\",\"name\":\"web\",\"kind\":\"publishable\"," +
            "\"dailyCreditLimit\":10,\"usedToday\":14}]}"));
        using var client = Build(handler);

        var report = await client.GetUsageAsync();

        Assert.Equal(0, report.Keys[0].RemainingToday);
        // Absent counters read as 0: "absent" and "none" are the same for a count.
        Assert.Equal(0, report.Totals.Jobs);
        Assert.Equal(0, report.Totals.ActiveSessions);
    }

    [Fact]
    public async Task GetUsageAsync_reads_an_embed_tokens_report_which_carries_no_keys()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"range\":{\"from\":\"2026-09-01T00:00:00.000Z\",\"to\":\"2026-09-13T00:00:00.000Z\"}," +
            "\"groupBy\":\"origin\",\"totals\":{\"jobs\":2,\"credits\":4}," +
            "\"series\":[{\"key\":\"https://app.example\",\"label\":\"https://app.example\"," +
            "\"jobs\":2,\"credits\":4}]}"));
        using var client = Build(handler);

        var report = await client.GetUsageAsync(new UsageQuery { GroupBy = UsageGroupBy.Origin });

        Assert.Empty(report.Keys);
        Assert.Equal(UsageGroupBy.Origin, report.GroupBy);
        Assert.Equal(4, report.Bucket("https://app.example")!.Credits);
    }

    [Fact]
    public async Task GetUsageAsync_surfaces_a_backwards_range_as_invalid_input()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.BadRequest,
            "{\"error\":{\"code\":\"invalid_input\",\"message\":\"from must not be after to\"}}"));
        using var client = Build(handler);

        var error = await Assert.ThrowsAsync<SnapneditException>(() => client.GetUsageAsync(new UsageQuery
        {
            From = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        }));

        Assert.Equal(ErrorCode.InvalidInput, error.Code);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task GetUsageAsync_refuses_a_grouping_it_does_not_know()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"range\":{\"from\":\"a\",\"to\":\"b\"},\"groupBy\":\"galaxy\",\"totals\":{},\"series\":[]}"));
        using var client = Build(handler);

        var error = await Assert.ThrowsAsync<SnapneditException>(() => client.GetUsageAsync());

        Assert.Equal(ErrorCode.Internal, error.Code);
        Assert.Contains("galaxy", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateJobAsync_reports_a_cache_hit_as_cached_and_free()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"jobId\":\"job-2\",\"status\":{\"state\":\"succeeded\",\"outputAssetId\":\"out-1\"," +
            "\"download\":{\"url\":\"/_local/results/a.png?sig=x\",\"expiresAt\":\"2026-01-01T00:00:00.000Z\"}}," +
            "\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null," +
            "\"creditCost\":0,\"cached\":true,\"deliveryOnly\":false}"));
        using var client = Build(handler);

        var created = await client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId("asset-9"));

        Assert.True(created.Cached);
        Assert.True(created.WasCacheHit);
        Assert.Equal(0, created.CreditCost);
        Assert.False(created.DeliveryOnly);
        // The attribution survives the conversion to the flattened view.
        var view = created.ToJobView();
        Assert.True(view.Cached);
        Assert.Equal(0, view.CreditCost);
    }

    [Fact]
    public async Task GetJobAsync_reads_a_delivery_only_clone()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.OK,
            "{\"state\":\"succeeded\",\"outputAssetId\":\"out-1\",\"download\":null," +
            "\"input\":{\"kind\":\"asset\"},\"destination\":{\"type\":\"saved\",\"id\":\"dest-1\"}," +
            "\"delivery\":{\"status\":\"pending\",\"attempts\":0}," +
            "\"creditCost\":0,\"cached\":true,\"deliveryOnly\":true}"));
        using var client = Build(handler);

        var view = await client.GetJobAsync("job-3");

        Assert.True(view.DeliveryOnly);
        Assert.True(view.Cached);
        Assert.Equal(0, view.CreditCost);
        // Cached, but not settled: the bucket write is still the worker's job.
        Assert.False(view.IsSettled);
    }

    [Fact]
    public async Task A_job_body_without_the_usage_fields_reads_as_unbilled_and_uncached()
    {
        var handler = new FakeHttpMessageHandler(FakeHttpMessageHandler.Json(
            HttpStatusCode.Accepted,
            "{\"jobId\":\"job-4\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"}," +
            "\"destination\":null,\"delivery\":null}"));
        using var client = Build(handler);

        var created = await client.CreateJobAsync(Operations.Upscale, JobInput.FromAssetId("asset-9"));

        Assert.Equal(0, created.CreditCost);
        Assert.False(created.Cached);
        Assert.False(created.DeliveryOnly);
        Assert.False(created.WasCacheHit);
    }

    [Fact]
    public async Task RunAsync_carries_the_jobs_attribution_onto_the_result()
    {
        var handler = new FakeHttpMessageHandler(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadCreated),
            FakeHttpMessageHandler.Empty(HttpStatusCode.NoContent),
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, UploadConfirmed),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.Accepted,
                "{\"jobId\":\"job-5\",\"status\":{\"state\":\"queued\"},\"input\":{\"kind\":\"asset\"}," +
                "\"destination\":null,\"delivery\":null,\"creditCost\":2,\"cached\":false,\"deliveryOnly\":false}"),
            FakeHttpMessageHandler.Json(
                HttpStatusCode.OK,
                "{\"state\":\"succeeded\",\"outputAssetId\":\"out-5\"," +
                "\"download\":{\"url\":\"/_local/results/b.png?sig=x\",\"expiresAt\":\"2026-01-01T00:00:00.000Z\"}," +
                "\"input\":{\"kind\":\"asset\"},\"destination\":null,\"delivery\":null," +
                "\"creditCost\":2,\"cached\":false,\"deliveryOnly\":false}"),
            FakeHttpMessageHandler.Binary(HttpStatusCode.OK, new byte[] { 9, 9 }, "image/png"));
        using var client = Build(handler);

        var result = await client.RunAsync(
            Operations.Upscale,
            JobInput.FromBytes(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, "image/png"),
            new RunOptions { PollInterval = TimeSpan.FromMilliseconds(1) });

        Assert.Equal(2, result.CreditCost);
        Assert.False(result.Cached);
        Assert.False(result.DeliveryOnly);
    }
}
